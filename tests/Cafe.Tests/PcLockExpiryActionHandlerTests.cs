using Cafe.Server.Agents;
using Cafe.Server.Data;
using Cafe.Shared;
using Cafe.Shared.Agents;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>WR-01: the expiry lock reports failure so the evaluator's at-least-once retry runs.</summary>
public class PcLockExpiryActionHandlerTests : AgentTestBase
{
    private PcLockExpiryActionHandler Handler() => new(Factory.Services.GetRequiredService<AgentCommandService>());

    private static Station Pc(int id, string machineId) =>
        new() { Id = id, Name = "PC-01", Kind = StationKind.PC, AgentMachineId = machineId };

    [Fact]
    public async Task Offline_agent_makes_the_handler_fail_so_it_is_retried()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler().ExecuteAsync(Pc(stationId, "pc-01"), CancellationToken.None));
    }

    [Fact]
    public async Task Connected_agent_receives_the_time_is_up_lock()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = new RecordingAgentClient(Factory, await KeyAsync(), "pc-01");
        await agent.StartAsync();
        await WaitUntilAsync(() => agent.Calls.Count >= 1, "initial push");

        await Handler().ExecuteAsync(Pc(stationId, "pc-01"), CancellationToken.None);

        await WaitUntilAsync(() => agent.Count("Lock") == 2, "expiry lock");
        Assert.Equal(PcLockExpiryActionHandler.Reason, ((LockInfo)agent.Calls[^1].Args[0]!).Reason);
    }

    [Fact]
    public async Task Stations_that_are_not_pc_agent_stations_are_a_quiet_no_op()
    {
        await Handler().ExecuteAsync(new Station { Id = 99, Name = "PS5", Kind = StationKind.Console }, CancellationToken.None);
        await Handler().ExecuteAsync(new Station { Id = 98, Name = "PC-X", Kind = StationKind.PC }, CancellationToken.None);
    }

    [Fact]
    public async Task A_retried_expiry_never_locks_a_pc_that_runs_a_newer_session()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = new RecordingAgentClient(Factory, await KeyAsync(), "pc-01");
        await agent.StartAsync();
        await WaitUntilAsync(() => agent.Calls.Count >= 1, "initial push");
        await PrepaidAsync(stationId, 60);
        await WaitUntilAsync(() => agent.Count("Unlock") == 1, "unlock");

        await Handler().ExecuteAsync(Pc(stationId, "pc-01"), CancellationToken.None);
        await Task.Delay(200);

        Assert.Equal(1, agent.Count("Lock")); // only the initial lock
    }
}
