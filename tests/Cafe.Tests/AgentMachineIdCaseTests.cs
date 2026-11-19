using Cafe.Server.Agents;
using Cafe.Shared.Agents;
using Cafe.Tests.Infrastructure;

namespace Cafe.Tests;

public class AgentMachineIdCaseTests : AgentTestBase
{
    [Fact]
    public async Task Agent_whose_machine_id_differs_only_in_case_still_matches_its_station()
    {
        await PcStationAsync("PC-01", "Desk-1");
        await using var agent = new RecordingAgentClient(Factory, await KeyAsync(), "DESK-1");
        await agent.StartAsync();
        await WaitUntilAsync(() => agent.Calls.Count >= 1, "initial push");

        var lockInfo = (LockInfo)agent.Calls[0].Args[0]!;
        Assert.Equal("PC-01", lockInfo.StationName);
        Assert.Equal(AgentCommandService.DefaultLockReason, lockInfo.Reason);
    }
}
