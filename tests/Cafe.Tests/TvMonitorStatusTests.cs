using Cafe.Server.Dashboard;
using Cafe.Server.Data;
using Cafe.Server.Devices;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Cafe.Tests;

public class TvMonitorStatusTests : SessionTestBase
{
    private readonly FakeTvDriver _driver = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    private static IOptions<TvOptions> Opts(int rest = 1) => Options.Create(new TvOptions { RestTimeoutSeconds = rest });

    private TvStateCache NewCache() => new(_clock, Opts());

    private TvMonitorService Monitor(TvStateCache cache, int rest = 1) =>
        new(Factory.Services.GetRequiredService<IDbContextFactory<CafeDbContext>>(), _driver, cache, Opts(rest), NullLogger<TvMonitorService>.Instance);

    private async Task<int> ConsoleAsync(string name, string? ip = "10.0.0.5", bool enabled = true)
    {
        using var scope = Factory.Services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
        {
            Name = name, StationTypeId = await TypeAsync(), Kind = StationKind.Console, TvIpAddress = ip, IsEnabled = enabled,
        });
        Assert.True(created.Success, created.Error);
        return created.Value!.Id;
    }

    [Fact]
    public async Task Poll_stores_each_state_and_skips_pcs_disabled_and_ip_less_stations()
    {
        var a = await ConsoleAsync("PS5-01");
        var b = await ConsoleAsync("PS5-02");
        await ConsoleAsync("PS5-03", ip: null);
        await ConsoleAsync("PS5-04", enabled: false);
        await StationAsync("PC-01");
        _driver.State = id => id == a ? TvPowerState.On : TvPowerState.Standby;
        var cache = NewCache();

        await Monitor(cache).PollOnceAsync(CancellationToken.None);

        Assert.Equal(TvPowerState.On, cache.Get(a));
        Assert.Equal(TvPowerState.Standby, cache.Get(b));
        Assert.Equal(2, cache.KnownStationIds.Count);
    }

    [Fact]
    public async Task One_hanging_tv_does_not_hold_up_the_others_and_reads_unreachable()
    {
        var hung = await ConsoleAsync("PS5-01");
        var ok = await ConsoleAsync("PS5-02");
        var thrower = await ConsoleAsync("PS5-03");
        _driver.State = _ => TvPowerState.On;
        _driver.StateHook = id => id == hung ? new TaskCompletionSource().Task : id == thrower ? throw new InvalidOperationException("x") : Task.CompletedTask;
        var cache = NewCache();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Monitor(cache).PollOnceAsync(CancellationToken.None);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"pass took {watch.Elapsed}");
        Assert.Equal(TvPowerState.Unreachable, cache.Get(hung));
        Assert.Equal(TvPowerState.On, cache.Get(ok));
        Assert.Equal(TvPowerState.Unreachable, cache.Get(thrower));
    }

    [Fact]
    public async Task Stations_that_lose_their_tv_or_are_disabled_leave_the_cache()
    {
        var a = await ConsoleAsync("PS5-01");
        var cache = NewCache();
        var monitor = Monitor(cache);
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.NotNull(cache.Get(a));

        await using (var db = await DbAsync())
        {
            (await db.Stations.SingleAsync(s => s.Id == a)).IsEnabled = false;
            await db.SaveChangesAsync();
        }

        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Null(cache.Get(a));
    }

    [Fact]
    public async Task Changed_fires_once_per_state_change_and_not_on_repeats()
    {
        var a = await ConsoleAsync("PS5-01");
        var cache = NewCache();
        var changes = new List<int>();
        cache.Changed += changes.Add;
        var monitor = Monitor(cache);

        await monitor.PollOnceAsync(CancellationToken.None);
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Single(changes);

        _driver.State = _ => TvPowerState.Standby;
        await monitor.PollOnceAsync(CancellationToken.None);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, id => Assert.Equal(a, id));
    }

    [Fact]
    public async Task Hosted_loop_stops_on_cancellation()
    {
        await ConsoleAsync("PS5-01");
        var cache = NewCache();
        var monitor = Monitor(cache);
        await monitor.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await monitor.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Single(cache.KnownStationIds);
    }

    [Fact]
    public void RunMonitor_false_disables_the_monitor()
    {
        var off = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Tv:RunMonitor"] = "false" }).Build();
        Assert.False(TvMonitorService.IsEnabled(off));
        Assert.True(TvMonitorService.IsEnabled(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
    }

    private static Station TvConsole(int id = 1) => new() { Id = id, Kind = StationKind.Console, TvIpAddress = "10.0.0.5" };

    [Theory]
    [InlineData(null, StationStatus.Offline)]
    [InlineData(TvPowerState.Unreachable, StationStatus.Offline)]
    [InlineData(TvPowerState.On, StationStatus.Idle)]
    [InlineData(TvPowerState.Standby, StationStatus.Idle)]
    [InlineData(TvPowerState.Unknown, StationStatus.Idle)]
    public void Provider_maps_the_cached_tv_state_for_an_idle_console(TvPowerState? state, StationStatus expected)
    {
        var cache = NewCache();
        if (state is { } s)
        {
            cache.Set(1, s);
        }

        Assert.Equal(expected, new DefaultStationStatusProvider(null, cache).GetStatus(TvConsole()));
    }

    [Fact]
    public void Provider_treats_a_stale_entry_as_offline()
    {
        var cache = NewCache();
        cache.Set(1, TvPowerState.On);
        var provider = new DefaultStationStatusProvider(null, cache);
        Assert.Equal(StationStatus.Idle, provider.GetStatus(TvConsole()));

        _clock.Advance(TimeSpan.FromSeconds(3 * 15 + 6));
        Assert.Equal(StationStatus.Offline, provider.GetStatus(TvConsole()));
    }

    [Fact]
    public void Parameterless_provider_and_pc_rules_are_unchanged()
    {
        Assert.Equal(StationStatus.Offline, new DefaultStationStatusProvider().GetStatus(TvConsole()));
        var withCache = new DefaultStationStatusProvider(null, NewCache());
        Assert.Equal(StationStatus.Idle, withCache.GetStatus(new Station { Kind = StationKind.PC }));
        Assert.Equal(StationStatus.Offline, withCache.GetStatus(new Station { Kind = StationKind.PC, AgentMachineId = "pc-01" }));
        Assert.Equal(StationStatus.Idle, withCache.GetStatus(new Station { Kind = StationKind.Console }));
    }

    [Fact]
    public void An_active_session_overrides_an_unreachable_tv()
    {
        var cache = NewCache();
        cache.Set(1, TvPowerState.Unreachable);
        var provider = new SessionAwareStatusProvider(new DefaultStationStatusProvider(null, cache));
        var now = DateTimeOffset.UtcNow;
        var session = new SessionCardModel(1, SessionMode.OpenEnded, SessionStatus.Active, null, 0, now, null, 0, 0, 0m, now);

        Assert.Equal(StationStatus.Offline, provider.GetStatus(TvConsole(), null, now));
        Assert.Equal(StationStatus.InSession, provider.GetStatus(TvConsole(), session, now));
    }

    [Fact]
    public async Task Cards_carry_the_tv_fields()
    {
        var console = await ConsoleAsync("PS5-01");
        var bare = await ConsoleAsync("PS5-02", ip: null);
        var pc = await StationAsync("PC-01");
        var cache = Factory.Services.GetRequiredService<TvStateCache>();
        cache.Set(console, TvPowerState.Standby);

        using var scope = Factory.Services.CreateScope();
        var cards = await scope.ServiceProvider.GetRequiredService<DashboardService>().GetCardsAsync();

        var tvCard = cards.Single(c => c.StationId == console);
        Assert.True(tvCard.HasTv);
        Assert.Equal(TvPowerState.Standby, tvCard.TvState);
        Assert.Equal(StationStatus.Idle, tvCard.Status);
        Assert.False(cards.Single(c => c.StationId == bare).HasTv);
        var pcCard = cards.Single(c => c.StationId == pc);
        Assert.False(pcCard.HasTv);
        Assert.Null(pcCard.TvState);
    }

    [Fact]
    public async Task Card_tv_state_is_null_without_a_cache_entry()
    {
        var console = await ConsoleAsync("PS5-01");
        using var scope = Factory.Services.CreateScope();
        var card = (await scope.ServiceProvider.GetRequiredService<DashboardService>().GetCardsAsync()).Single(c => c.StationId == console);

        Assert.True(card.HasTv);
        Assert.Null(card.TvState);
        Assert.Equal(StationStatus.Offline, card.Status);
    }
}
