using Cafe.Server.Dashboard;
using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class SessionTracerTests : IDisposable
{
    private readonly CafeWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<int> StationAsync(string name = "PC-01", decimal rate = 6000m, bool enabled = true, string? agent = null, int sort = 0)
    {
        using var scope = _factory.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<StationTypeService>();
        var typeId = (await types.ListAsync()).FirstOrDefault()?.Id
                     ?? (await types.CreateAsync(new StationTypeInput { Name = "Standard", HourlyRate = rate })).Value!.Id;
        var created = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
        {
            Name = name, StationTypeId = typeId, Kind = StationKind.PC, IsEnabled = enabled, AgentMachineId = agent, SortOrder = sort,
        });
        Assert.True(created.Success, created.Error);
        return created.Value!.Id;
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    private Task<ServiceResult<Session>> StartAsync(int stationId, int minutes = 60) =>
        InScopeAsync(sp => sp.GetRequiredService<SessionService>().StartPrepaidAsync(stationId, minutes));

    private Task<List<StationCardModel>> CardsAsync() =>
        InScopeAsync(sp => sp.GetRequiredService<DashboardService>().GetCardsAsync());

    private async Task<CafeDbContext> DbAsync() =>
        await _factory.Services.GetRequiredService<IDbContextFactory<CafeDbContext>>().CreateDbContextAsync();

    [Fact]
    public async Task Start_prepaid_persists_session_open_segment_started_event_and_quote()
    {
        var stationId = await StationAsync();

        var result = await StartAsync(stationId, 60);

        Assert.True(result.Success, result.Error);
        await using var db = await DbAsync();
        var session = await db.Sessions.Include(s => s.Segments).Include(s => s.Events).SingleAsync();
        Assert.Equal(SessionMode.Prepaid, session.Mode);
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal(60, session.PurchasedMinutes);
        Assert.Equal(6000.00m, session.ChargedAmount);
        Assert.Equal(_factory.Clock.GetUtcNow(), session.StartedAtUtc);
        Assert.Null(Assert.Single(session.Segments).EndUtc);
        Assert.Equal(SessionEventType.Started, Assert.Single(session.Events).Type);
    }

    [Fact]
    public async Task Card_shows_in_session_with_remaining_time_that_follows_the_clock()
    {
        var stationId = await StationAsync();
        await StartAsync(stationId, 60);

        var card = Assert.Single(await CardsAsync());
        Assert.Equal(StationStatus.InSession, card.Status);
        var session = Assert.IsType<SessionCardModel>(card.Session);
        var now = _factory.Clock.GetUtcNow();
        Assert.Equal(3600, Remaining(session, now));

        _factory.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(3000, Remaining(session, _factory.Clock.GetUtcNow()));
    }

    private static long Remaining(SessionCardModel s, DateTimeOffset now) =>
        SessionClock.RemainingSeconds(s.PurchasedMinutes!.Value, SessionClock.ActiveSeconds(s.ClosedActiveSeconds, s.OpenSegmentStartUtc, now));

    [Fact]
    public async Task Stop_returns_the_quoted_charge_closes_the_segment_and_frees_the_station()
    {
        var stationId = await StationAsync();
        var session = (await StartAsync(stationId, 60)).Value!;
        _factory.Clock.Advance(TimeSpan.FromMinutes(5));

        var stop = await InScopeAsync(sp => sp.GetRequiredService<SessionService>().StopAsync(session.Id));

        Assert.True(stop.Success, stop.Error);
        Assert.Equal(6000.00m, stop.Value);
        await using var db = await DbAsync();
        var saved = await db.Sessions.Include(s => s.Segments).Include(s => s.Events).SingleAsync();
        Assert.Equal(SessionStatus.Ended, saved.Status);
        Assert.Equal(SessionEndReason.Stopped, saved.EndReason);
        Assert.Equal(_factory.Clock.GetUtcNow(), saved.EndedAtUtc);
        Assert.NotNull(Assert.Single(saved.Segments).EndUtc);
        Assert.Contains(saved.Events, e => e.Type == SessionEventType.Stopped);

        var card = Assert.Single(await CardsAsync());
        Assert.Equal(StationStatus.Idle, card.Status);
        Assert.Null(card.Session);

        var again = await InScopeAsync(sp => sp.GetRequiredService<SessionService>().StopAsync(session.Id));
        Assert.False(again.Success);
    }

    [Fact]
    public async Task Second_session_on_a_station_is_rejected_by_service_and_by_the_filtered_unique_index()
    {
        var stationId = await StationAsync();
        var first = (await StartAsync(stationId)).Value!;

        var second = await StartAsync(stationId);
        Assert.False(second.Success);
        Assert.Equal(FailureKind.Validation, second.Kind);

        await using (var db = await DbAsync())
        {
            db.Sessions.Add(NewSession(stationId, SessionStatus.Active));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await InScopeAsync(sp => sp.GetRequiredService<SessionService>().StopAsync(first.Id));
        await using var db2 = await DbAsync();
        db2.Sessions.Add(NewSession(stationId, SessionStatus.Active));
        await db2.SaveChangesAsync();
    }

    private Session NewSession(int stationId, SessionStatus status) => new()
    {
        StationId = stationId,
        Mode = SessionMode.OpenEnded,
        Status = status,
        StartedAtUtc = _factory.Clock.GetUtcNow(),
    };

    [Fact]
    public async Task Start_validates_minutes_station_existence_and_enabled()
    {
        var stationId = await StationAsync("On");
        var disabledId = await StationAsync("Off", enabled: false);

        Assert.False((await StartAsync(stationId, 0)).Success);
        Assert.False((await StartAsync(stationId, 1441)).Success);
        var disabled = await StartAsync(disabledId);
        Assert.False(disabled.Success);
        Assert.Equal(FailureKind.Validation, disabled.Kind);
        var unknown = await StartAsync(99999);
        Assert.Equal(FailureKind.NotFound, unknown.Kind);

        await using var db = await DbAsync();
        Assert.Empty(db.Sessions);
        Assert.True((await StartAsync(stationId, 1)).Success);
    }

    [Fact]
    public async Task Offline_agent_station_can_start_and_session_takes_precedence()
    {
        var stationId = await StationAsync(agent: "pc-01");
        Assert.Equal(StationStatus.Offline, Assert.Single(await CardsAsync()).Status);

        var result = await StartAsync(stationId);

        Assert.True(result.Success, result.Error);
        Assert.Equal(StationStatus.InSession, Assert.Single(await CardsAsync()).Status);
    }

    [Fact]
    public async Task Notifier_is_raised_and_a_throwing_subscriber_does_not_break_the_mutation()
    {
        var stationId = await StationAsync();
        var notifier = _factory.Services.GetRequiredService<SessionNotifier>();
        var seen = new List<int>();
        notifier.Changed += _ => throw new InvalidOperationException("boom");
        notifier.Changed += seen.Add;

        Assert.True((await StartAsync(stationId)).Success);

        Assert.Equal([stationId], seen);
    }

    [Fact]
    public async Task Home_page_renders_countdown_and_stop_for_a_session_and_start_for_an_idle_station()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        var busy = await StationAsync("PC-01", sort: 0);
        await StationAsync("PC-02", sort: 1);
        await StartAsync(busy, 60);

        var body = await client.GetStringAsync("/");

        var busyCard = Slice(body, "PC-01");
        Assert.Contains("1:00:00", busyCard);
        Assert.Contains(">Stop<", busyCard);
        Assert.Contains("In session", busyCard);
        var idleCard = Slice(body, "PC-02");
        Assert.Contains("Start open-ended", idleCard);
    }

    private static string Slice(string html, string name)
    {
        var start = html.IndexOf($">{name}</h2>", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} card not found");
        var next = html.IndexOf("station-card", start, StringComparison.Ordinal);
        return next < 0 ? html[start..] : html[start..next];
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(-5, "00:00")]
    [InlineData(59, "00:59")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3661, "1:01:01")]
    [InlineData(90000, "25:00:00")]
    public void Countdown_format(long seconds, string expected) =>
        Assert.Equal(expected, SessionClock.FormatCountdown(seconds));

    [Theory]
    [InlineData(SessionStatus.Active, SessionMode.Prepaid, 3600, 10, StationStatus.InSession)]
    [InlineData(SessionStatus.Active, SessionMode.Prepaid, 600, 10, StationStatus.Expiring)]
    [InlineData(SessionStatus.Active, SessionMode.OpenEnded, 0, 10, StationStatus.InSession)]
    [InlineData(SessionStatus.Paused, SessionMode.Prepaid, 100, 10, StationStatus.Paused)]
    [InlineData(SessionStatus.Grace, SessionMode.Prepaid, 0, 10, StationStatus.Expiring)]
    public void Status_mapping(SessionStatus status, SessionMode mode, long remaining, int warn, StationStatus expected) =>
        Assert.Equal(expected, SessionStatusMapper.Map(status, mode, remaining, warn));

    [Fact]
    public async Task Settings_default_and_tolerate_malformed_values()
    {
        var settings = _factory.Services.GetRequiredService<SessionSettingsService>();
        var defaults = await settings.GetAsync();
        Assert.Equal(2, defaults.GracePeriodMinutes);
        Assert.Equal([10, 5, 1], defaults.WarningMinutes);

        await using (var db = await DbAsync())
        {
            db.Settings.Add(new Setting { Key = SessionSettingsService.GraceKey, Value = "abc" });
            db.Settings.Add(new Setting { Key = SessionSettingsService.WarningKey, Value = "15, 3" });
            await db.SaveChangesAsync();
        }

        var loaded = await settings.GetAsync();
        Assert.Equal(2, loaded.GracePeriodMinutes);
        Assert.Equal([15, 3], loaded.WarningMinutes);
    }
}
