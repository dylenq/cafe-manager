using Cafe.Server.Dashboard;
using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cafe.Tests;

public class DashboardSessionCardsTests : SessionTestBase
{
    private async Task<string> PageAsync()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        return await client.GetStringAsync("/");
    }

    /// <summary>The slice of the page from the station's name to the next card (or end).</summary>
    private static string Card(string html, string name)
    {
        var start = html.IndexOf($">{name}</h2>", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} card not found");
        var next = html.IndexOf("station-card", start, StringComparison.Ordinal);
        return next < 0 ? html[start..] : html[start..next];
    }

    [Fact]
    public async Task Idle_card_offers_start_controls()
    {
        await StationAsync("PC-01");

        var card = Card(await PageAsync(), "PC-01");

        Assert.Contains("Idle", card);
        Assert.Contains("Start open-ended", card);
        Assert.Contains("Start prepaid", card);
        foreach (var m in new[] { "30", "60", "120", "180" })
        {
            Assert.Contains($">{m}</button>", card);
        }

        Assert.DoesNotContain("Pause", card);
        Assert.DoesNotContain("Extend", card);
    }

    [Fact]
    public async Task Prepaid_card_shows_countdown_and_all_controls()
    {
        var a = await StationAsync("PC-01");
        await StationAsync("PC-02");
        await PrepaidAsync(a, 60);

        var card = Card(await PageAsync(), "PC-01");

        Assert.Contains("Prepaid", card);
        Assert.Contains("1:00:00", card);
        Assert.Contains("In session", card);
        Assert.Contains("Pause", card);
        Assert.Contains("Extend", card);
        Assert.Contains("Move", card);
        Assert.Contains("Stop", card);
        Assert.Contains("PC-02", card);
        Assert.DoesNotContain("Start open-ended", card);
    }

    [Fact]
    public async Task Open_ended_card_shows_elapsed_and_no_extend()
    {
        var a = await StationAsync("PC-01");
        await OpenEndedAsync(a);
        Advance(TimeSpan.FromMinutes(5));

        var card = Card(await PageAsync(), "PC-01");

        Assert.Contains("Open-ended", card);
        Assert.Contains("05:00", card);
        Assert.Contains("est.", card);
        Assert.DoesNotContain("Extend", card);
        Assert.Contains("Pause", card);
    }

    [Fact]
    public async Task Grace_card_shows_time_up_and_grace_countdown()
    {
        var a = await StationAsync("PC-01");
        var session = await PrepaidAsync(a, 1);
        Advance(TimeSpan.FromMinutes(2));
        await UpdateAsync(session.Id, s =>
        {
            s.Status = SessionStatus.Grace;
            s.GraceStartedAtUtc = Factory.Clock.GetUtcNow();
        });

        var page = await PageAsync();
        var card = Card(page, "PC-01");

        Assert.Contains("TIME UP", card);
        Assert.Contains("Expiring", card);
        Assert.Contains("alert-timeup-level", page);
        Assert.Contains("Grace", card);
    }

    [Fact]
    public async Task Paused_card_shows_resume_and_paused_badge()
    {
        var a = await StationAsync("PC-01");
        var session = await PrepaidAsync(a, 60);
        Assert.True((await SvcAsync(s => s.PauseAsync(session.Id))).Success);

        var card = Card(await PageAsync(), "PC-01");

        Assert.Contains("Paused", card);
        Assert.Contains("Resume", card);
    }

    [Fact]
    public async Task Agent_station_without_session_shows_offline_and_start_controls()
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
            {
                Name = "PC-AG", StationTypeId = await TypeAsync(), Kind = StationKind.PC, AgentMachineId = "pc-ag",
            });
            Assert.True(created.Success, created.Error);
        }

        var card = Card(await PageAsync(), "PC-AG");

        Assert.Contains("Offline", card);
        Assert.Contains("Start open-ended", card);
    }

    [Fact]
    public async Task Session_status_overrides_offline()
    {
        int id;
        using (var scope = Factory.Services.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
            {
                Name = "PC-AG", StationTypeId = await TypeAsync(), Kind = StationKind.PC, AgentMachineId = "pc-ag",
            });
            id = created.Value!.Id;
        }

        await PrepaidAsync(id, 60);

        var card = Card(await PageAsync(), "PC-AG");

        Assert.Contains("In session", card);
        Assert.DoesNotContain("Offline", card);
    }

    private sealed class RecordingHandler : IExpiryActionHandler
    {
        public int Calls { get; private set; }

        public Task ExecuteAsync(Station station, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private async Task<List<StationCardModel>> CardsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DashboardService>().GetCardsAsync();
    }

    [Fact]
    public async Task End_to_end_prepaid_expiry_warning_grace_and_handler()
    {
        var typeId = await TypeAsync("Rig", 6000m);
        await RuleAsync(RateRuleKind.Multiplier, 1m, new TimeOnly(0, 0), new TimeOnly(23, 59), typeId);
        var a = await StationAsync("PC-A", typeId);
        await StationAsync("PC-B", typeId);
        var session = await PrepaidAsync(a, 15);
        var handler = new RecordingHandler();
        var sp = Factory.Services;
        var evaluator = new ExpiryEvaluator(
            sp.GetRequiredService<IDbContextFactory<CafeDbContext>>(), Factory.Clock,
            sp.GetRequiredService<SessionSettingsService>(), sp.GetRequiredService<SessionNotifier>(),
            [handler], NullLogger<ExpiryEvaluator>.Instance);

        // One login up front: antiforgery and cookies are time-bound and the clock jumps below.
        using var client = await Factory.CreateAuthenticatedClientAsync();
        var page = await client.GetStringAsync("/");
        Assert.Contains("In session", Card(page, "PC-A"));
        Assert.Contains("15:00", Card(page, "PC-A"));

        // Inside the warning window (default first warning at 10 minutes left).
        Advance(TimeSpan.FromMinutes(6));
        await evaluator.EvaluateAsync();
        var warned = (await CardsAsync()).Single(c => c.Name == "PC-A");
        Assert.Equal(StationStatus.Expiring, warned.Status);
        Assert.Equal(AlertLevel.Warning, SessionAlerts.GetLevel(warned.Session!, Factory.Clock.GetUtcNow()));

        // Past expiry: grace starts and the card says TIME UP.
        Advance(TimeSpan.FromMinutes(10));
        await evaluator.EvaluateAsync();
        var graced = (await CardsAsync()).Single(c => c.Name == "PC-A");
        Assert.Equal(AlertLevel.TimeUp, SessionAlerts.GetLevel(graced.Session!, Factory.Clock.GetUtcNow()));
        Assert.Contains("TIME UP", Card(await client.GetStringAsync("/"), "PC-A"));
        Assert.Equal(0, handler.Calls);

        // Past grace (default 2 minutes): station is idle again, handler fired exactly once.
        Advance(TimeSpan.FromMinutes(3));
        await evaluator.EvaluateAsync();
        await evaluator.EvaluateAsync();
        var done = (await CardsAsync()).Single(c => c.Name == "PC-A");
        Assert.Equal(StationStatus.Idle, done.Status);
        Assert.Equal(1, handler.Calls);

        var history = await client.GetStringAsync("/sessions");
        Assert.Contains("PC-A", history);
        Assert.Contains("Expired", history);

        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionEndReason.Expired, saved.EndReason);
        var order = saved.Events.OrderBy(e => e.Id).Select(e => e.Type)
            .Where(t => t is SessionEventType.WarningReached or SessionEventType.Expired
                or SessionEventType.GraceElapsed or SessionEventType.ExpiryActionFired)
            .Distinct().ToList();
        Assert.Equal(
            [SessionEventType.WarningReached, SessionEventType.Expired, SessionEventType.GraceElapsed, SessionEventType.ExpiryActionFired],
            order);
    }

    [Fact]
    public async Task End_to_end_open_ended_pause_resume_move_stop_audit_and_charge()
    {
        var typeId = await TypeAsync("Rig", 6000m);
        var a = await StationAsync("PC-A", typeId);
        var b = await StationAsync("PC-B", typeId);
        var session = await OpenEndedAsync(b);

        Advance(TimeSpan.FromMinutes(10));
        Assert.True((await SvcAsync(s => s.PauseAsync(session.Id))).Success);
        Advance(TimeSpan.FromMinutes(5));
        Assert.True((await SvcAsync(s => s.ResumeAsync(session.Id))).Success);
        Advance(TimeSpan.FromMinutes(5));
        Assert.True((await SvcAsync(s => s.MoveAsync(session.Id, a))).Success);
        Advance(TimeSpan.FromMinutes(10));
        var stop = await SvcAsync(s => s.StopAsync(session.Id));

        Assert.True(stop.Success, stop.Error);
        // 10 + 5 + 10 = 25 active minutes at 6000/hour (the paused 5 minutes are not billed).
        Assert.Equal(2500.00m, stop.Value);
        var saved = await LoadAsync(session.Id);
        Assert.Equal(2500.00m, saved.ChargedAmount);
        var types = saved.Events.OrderBy(e => e.Id).Select(e => e.Type).ToList();
        Assert.Equal(
            [SessionEventType.Started, SessionEventType.Paused, SessionEventType.Resumed, SessionEventType.Moved, SessionEventType.Stopped],
            types);
    }
}
