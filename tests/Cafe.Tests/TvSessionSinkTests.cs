using Cafe.Server.Data;
using Cafe.Server.Devices;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cafe.Tests;

public class TvSessionSinkTests : SessionTestBase
{
    private readonly FakeTvDriver _driver = new();
    private readonly List<string> _notices = [];

    public TvSessionSinkTests()
    {
        Factory.ServiceOverrides.Add(services =>
        {
            services.RemoveAll<ITvDriver>();
            services.AddSingleton<ITvDriver>(_driver);
        });
        Factory.ExtraSettings["Tv:ActionTimeoutSeconds"] = "1";
        Cache.NoticeRaised += _notices.Add;
    }

    private TvStateCache Cache => Factory.Services.GetRequiredService<TvStateCache>();

    private TvActionQueue Queue => Factory.Services.GetRequiredService<TvActionQueue>();

    private async Task<int> ConsoleAsync(string name = "PS5-01", string? ip = "10.0.0.5")
    {
        using var scope = Factory.Services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
        {
            Name = name, StationTypeId = await TypeAsync(), Kind = StationKind.Console, TvIpAddress = ip,
        });
        Assert.True(created.Success, created.Error);
        return created.Value!.Id;
    }

    private async Task<List<AgentCommandLog>> LogsAsync()
    {
        await using var db = await DbAsync();
        return await db.AgentCommandLogs.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
    }

    [Fact]
    public async Task Start_powers_the_tv_on_for_a_console_with_a_tv()
    {
        var id = await ConsoleAsync();
        await PrepaidAsync(id);
        await Queue.WhenIdleAsync();

        Assert.Equal(["PowerOn"], _driver.Actions(id));
        Assert.Equal(TvPowerState.On, Cache.Get(id));
    }

    [Fact]
    public async Task Start_does_nothing_for_a_pc_or_a_console_without_a_tv_address()
    {
        var pc = await StationAsync("PC-01");
        var bare = await ConsoleAsync("PS5-02", ip: null);
        await PrepaidAsync(pc);
        await PrepaidAsync(bare);
        await Queue.WhenIdleAsync();

        Assert.Empty(_driver.Calls);
    }

    [Fact]
    public async Task Pause_does_nothing_and_Resume_powers_on()
    {
        var id = await ConsoleAsync();
        var session = await PrepaidAsync(id);
        await Queue.WhenIdleAsync();
        Assert.True((await SvcAsync(s => s.PauseAsync(session.Id))).Success);
        await Queue.WhenIdleAsync();
        Assert.Equal(["PowerOn"], _driver.Actions(id));

        Assert.True((await SvcAsync(s => s.ResumeAsync(session.Id))).Success);
        await Queue.WhenIdleAsync();
        Assert.Equal(["PowerOn", "PowerOn"], _driver.Actions(id));
    }

    [Fact]
    public async Task Stop_powers_the_tv_off_by_default()
    {
        var id = await ConsoleAsync();
        var session = await PrepaidAsync(id);
        await Queue.WhenIdleAsync();
        Assert.True((await SvcAsync(s => s.StopAsync(session.Id))).Success);
        await Queue.WhenIdleAsync();

        Assert.Equal(["PowerOn", "PowerOff"], _driver.Actions(id));
    }

    [Fact]
    public async Task Stop_leaves_the_tv_on_when_the_setting_is_false()
    {
        await Factory.Services.GetRequiredService<TvSettingsService>().SetPowerOffOnStopAsync(false);
        var id = await ConsoleAsync();
        var session = await PrepaidAsync(id);
        await Queue.WhenIdleAsync();
        Assert.True((await SvcAsync(s => s.StopAsync(session.Id))).Success);
        await Queue.WhenIdleAsync();

        Assert.Equal(["PowerOn"], _driver.Actions(id));
    }

    [Fact]
    public async Task Move_powers_the_old_tv_off_and_the_new_one_on()
    {
        var a = await ConsoleAsync("PS5-01");
        var b = await ConsoleAsync("PS5-02");
        var session = await PrepaidAsync(a);
        await Queue.WhenIdleAsync();
        Assert.True((await SvcAsync(s => s.MoveAsync(session.Id, b))).Success);
        await Queue.WhenIdleAsync();

        Assert.Equal(["PowerOn", "PowerOff"], _driver.Actions(a));
        Assert.Equal(["PowerOn"], _driver.Actions(b));
    }

    [Fact]
    public async Task MovePaused_powers_only_the_old_tv_off()
    {
        var a = await ConsoleAsync("PS5-01");
        var b = await ConsoleAsync("PS5-02");
        var session = await PrepaidAsync(a);
        Assert.True((await SvcAsync(s => s.PauseAsync(session.Id))).Success);
        await Queue.WhenIdleAsync();
        Assert.True((await SvcAsync(s => s.MoveAsync(session.Id, b))).Success);
        await Queue.WhenIdleAsync();

        Assert.Equal(["PowerOn", "PowerOff"], _driver.Actions(a));
        Assert.Empty(_driver.Actions(b));
    }

    [Fact]
    public async Task StopThenStart_ends_with_the_tv_on_and_never_runs_the_stale_power_off()
    {
        _driver.Gate = new TaskCompletionSource();
        var id = await ConsoleAsync();
        var first = await PrepaidAsync(id);
        Assert.True((await SvcAsync(s => s.StopAsync(first.Id))).Success);
        await PrepaidAsync(id);
        _driver.Gate.SetResult();
        await Queue.WhenIdleAsync();

        var actions = _driver.Actions(id);
        Assert.Equal("PowerOn", actions[^1]);
        Assert.DoesNotContain("PowerOff", actions);
    }

    [Fact]
    public async Task DriverFailure_never_fails_the_session_and_is_logged_and_noticed_once()
    {
        _driver.Hook = (_, _) => throw new InvalidOperationException("boom");
        var id = await ConsoleAsync();
        var result = await SvcAsync(s => s.StartPrepaidAsync(id, 60));
        Assert.True(result.Success, result.Error);
        await Queue.WhenIdleAsync();

        var logs = await LogsAsync();
        var row = Assert.Single(logs);
        Assert.Equal(AgentCommandKind.TvPowerOn, row.Command);
        Assert.Contains("Failed", row.Details);
        Assert.Single(_notices);
        Assert.Contains("PS5-01", _notices[0]);
        Assert.Contains("did not respond", _notices[0]);

        Assert.True((await SvcAsync(s => s.StopAsync(result.Value!.Id))).Success);
        await Queue.WhenIdleAsync();
        Assert.Equal(2, (await LogsAsync()).Count);
        Assert.Single(_notices);
    }

    [Fact]
    public async Task DriverFailure_outcome_Failed_raises_a_notice()
    {
        _driver.OnResult = _ => new TvActionResult(TvActionOutcome.Failed, TvPowerState.Unknown, "The TV is not paired");
        var id = await ConsoleAsync();
        Assert.True((await SvcAsync(s => s.StartPrepaidAsync(id, 30))).Success);
        await Queue.WhenIdleAsync();

        Assert.Single(_notices);
        Assert.Contains("Failed", (await LogsAsync()).Single().Details);
    }

    [Fact]
    public async Task DriverHangs_does_not_delay_the_session_and_the_action_gives_up()
    {
        _driver.Hook = (_, _) => new TaskCompletionSource().Task;
        var id = await ConsoleAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await SvcAsync(s => s.StartPrepaidAsync(id, 60));
        watch.Stop();

        Assert.True(result.Success, result.Error);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"start took {watch.Elapsed}");

        await Queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("Failed", (await LogsAsync()).Single().Details);
        Assert.Single(_notices);
    }

    [Fact]
    public async Task ManualPowerOn_logs_with_the_Manual_source_and_updates_the_cache()
    {
        var id = await ConsoleAsync();
        var result = await Factory.Services.GetRequiredService<TvControlService>().PowerOnAsync(id);

        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        var row = Assert.Single(await LogsAsync());
        Assert.Equal(AgentCommandKind.TvPowerOn, row.Command);
        Assert.StartsWith("Manual", row.Details);
        Assert.Equal(TvPowerState.On, Cache.Get(id));

        var off = await Factory.Services.GetRequiredService<TvControlService>().PowerOffAsync(id);
        Assert.True(off.Success);
        Assert.Equal(AgentCommandKind.TvPowerOff, (await LogsAsync())[1].Command);
        Assert.Equal(TvPowerState.Standby, Cache.Get(id));
    }

    [Fact]
    public async Task ManualPowerOn_rejects_a_station_without_a_tv()
    {
        var pc = await StationAsync("PC-01");
        var result = await Factory.Services.GetRequiredService<TvControlService>().PowerOnAsync(pc);

        Assert.False(result.Success);
        Assert.Empty(_driver.Calls);
    }

    [Fact]
    public async Task ExpiryHandler_logs_updates_the_cache_and_throws_on_failure()
    {
        var id = await ConsoleAsync();
        var handler = Factory.Services.GetServices<IExpiryActionHandler>().OfType<TvExpiryActionHandler>().Single();
        Station station;
        await using (var db = await DbAsync())
        {
            station = await db.Stations.AsNoTracking().SingleAsync(s => s.Id == id);
        }

        await handler.ExecuteAsync(station, CancellationToken.None);
        var row = Assert.Single(await LogsAsync());
        Assert.StartsWith("Expiry", row.Details);
        Assert.Equal(AgentCommandKind.TvPowerOff, row.Command);
        Assert.Equal(TvPowerState.Standby, Cache.Get(id));

        _driver.OffResult = _ => new TvActionResult(TvActionOutcome.Failed, TvPowerState.Unknown, "nope");
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExecuteAsync(station, CancellationToken.None));
        _driver.OffResult = _ => new TvActionResult(TvActionOutcome.Unreachable, TvPowerState.Unreachable, "off");
        // Unreachable at expiry counts as already off (WR-01): no retry, a desk notice instead.
        await handler.ExecuteAsync(station, CancellationToken.None);
    }
}
