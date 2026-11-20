using Cafe.Server.Data;
using Cafe.Server.Devices;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cafe.Tests;

/// <summary>Regression tests for the Phase 4 code review fixes that need the full application.</summary>
public class TvReviewFixTests : SessionTestBase
{
    private async Task<int> PairedConsoleAsync(string ip = "10.0.0.5")
    {
        int id;
        using (var scope = Factory.Services.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
            {
                Name = "PS5-01", StationTypeId = await TypeAsync(), Kind = StationKind.Console, TvIpAddress = ip, TvMacAddress = "AA:BB:CC:DD:EE:FF",
            });
            Assert.True(created.Success, created.Error);
            id = created.Value!.Id;
        }

        await using var db = await DbAsync();
        await db.Stations.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.TvToken, "SECRET"));
        return id;
    }

    private async Task<Station> LoadStationAsync(int id)
    {
        await using var db = await DbAsync();
        return await db.Stations.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private async Task UpdateAsync(int id, Action<StationInput> change)
    {
        var current = await LoadStationAsync(id);
        var input = new StationInput
        {
            Name = current.Name,
            StationTypeId = current.StationTypeId,
            Kind = current.Kind,
            TvIpAddress = current.TvIpAddress,
            TvMacAddress = current.TvMacAddress,
            AgentMachineId = current.AgentMachineId,
            SortOrder = current.SortOrder,
            IsEnabled = current.IsEnabled,
        };
        change(input);
        using var scope = Factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<StationService>().UpdateAsync(id, input);
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task Changing_the_tv_ip_clears_the_pairing_token()
    {
        var id = await PairedConsoleAsync();
        await UpdateAsync(id, i => i.TvIpAddress = "10.0.0.6");
        Assert.Null((await LoadStationAsync(id)).TvToken);
    }

    [Fact]
    public async Task Turning_a_console_into_a_pc_clears_the_pairing_token()
    {
        var id = await PairedConsoleAsync();
        await UpdateAsync(id, i => { i.Kind = StationKind.PC; i.AgentMachineId = "PC-1"; });
        Assert.Null((await LoadStationAsync(id)).TvToken);
    }

    // ---- WR-05 ----

    private sealed class HangingDriver : ITvDriver
    {
        public Task<TvPairResult> PairAsync(Station station, CancellationToken ct) => throw new NotSupportedException();

        public Task<TvPowerState> GetStateAsync(Station station, CancellationToken ct) => throw new NotSupportedException();

        public Task<TvActionResult> PowerOnAsync(Station station, CancellationToken ct) => throw new NotSupportedException();

        public async Task<TvActionResult> PowerOffAsync(Station station, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new TvActionResult(TvActionOutcome.PoweredOff, TvPowerState.Standby, "unreachable code");
        }
    }

    private TvExpiryActionHandler HangingHandler(IOptions<TvOptions> options)
    {
        var dbFactory = Factory.Services.GetRequiredService<IDbContextFactory<CafeDbContext>>();
        var control = new TvControlService(new HangingDriver(), dbFactory, new TvStateCache(TimeProvider.System, options),
            TimeProvider.System, NullLogger<TvControlService>.Instance);
        return new TvExpiryActionHandler(control, dbFactory, options, NullLogger<TvExpiryActionHandler>.Instance);
    }

    [Fact]
    public void The_default_expiry_budget_fits_inside_the_evaluator_handler_timeout()
    {
        Assert.True(new TvOptions().ExpiryActionTimeoutSeconds < 10);
    }

    [Fact]
    public async Task A_busy_tv_at_expiry_fails_within_the_budget_as_a_retryable_error()
    {
        var id = await PairedConsoleAsync();
        var handler = HangingHandler(Options.Create(new TvOptions { ExpiryActionTimeoutSeconds = 1 }));
        var station = await LoadStationAsync(id);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExecuteAsync(station, default));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.Contains("retried", ex.Message);
    }

    [Fact]
    public async Task Caller_cancellation_at_expiry_is_not_turned_into_a_failure()
    {
        var id = await PairedConsoleAsync();
        var handler = HangingHandler(Options.Create(new TvOptions()));
        var station = await LoadStationAsync(id);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.ExecuteAsync(station, cts.Token));
    }

    [Fact]
    public async Task Unrelated_edits_keep_the_pairing_token()
    {
        var id = await PairedConsoleAsync();
        await UpdateAsync(id, i => { i.Name = "PS5-renamed"; i.TvMacAddress = "AA:BB:CC:DD:EE:00"; });
        Assert.Equal("SECRET", (await LoadStationAsync(id)).TvToken);
    }
}
