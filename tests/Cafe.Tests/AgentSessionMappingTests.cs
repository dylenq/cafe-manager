using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Shared.Agents;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>Proves the full session-to-command mapping through raw recording agents (server side of AGNT-03/04).</summary>
public class AgentSessionMappingTests : AgentTestBase
{
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    private async Task<RecordingAgentClient> ConnectAsync(string machineId, int expectedInitial = 1)
    {
        var recorder = new RecordingAgentClient(Factory, await KeyAsync(), machineId);
        await recorder.StartAsync();
        await WaitUntilAsync(() => recorder.Calls.Count >= expectedInitial, $"initial push for {machineId}");
        return recorder;
    }

    private static string[] Methods(RecordingAgentClient r) => r.Calls.Select(c => c.Method).ToArray();

    private static async Task WaitForAsync(RecordingAgentClient r, int count) =>
        await WaitUntilAsync(() => r.Calls.Count >= count, $"{count} calls (have {string.Join(",", Methods(r))})");

    private Task EvalAsync() => Factory.Services.GetRequiredService<IExpiryEvaluator>().EvaluateAsync(CancellationToken.None);

    private async Task SetSettingsAsync(int grace, string warnings)
    {
        var result = await Factory.Services.GetRequiredService<SessionSettingsService>()
            .SaveAsync(new SessionSettingsInput { GracePeriodMinutes = grace, WarningMinutes = warnings });
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task Lifecycle_maps_to_unlock_lock_unlock_updated_lock()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("pc-01");
        var session = await PrepaidAsync(stationId, 60);
        Assert.True((await SvcAsync(s => s.PauseAsync(session.Id))).Success);
        Assert.True((await SvcAsync(s => s.ResumeAsync(session.Id))).Success);
        Assert.True((await SvcAsync(s => s.ExtendAsync(session.Id, 15))).Success);
        Assert.True((await SvcAsync(s => s.StopAsync(session.Id))).Success);

        await WaitForAsync(agent, 6);

        Assert.Equal(["Lock", "Unlock", "Lock", "Unlock", "SessionUpdated", "Lock"], Methods(agent));
        Assert.Equal("Session paused", ((LockInfo)agent.Calls[2].Args[0]!).Reason);
        Assert.Equal(75 * 60, ((SessionInfo)agent.Calls[4].Args[0]!).RemainingSeconds);
    }

    [Fact]
    public async Task Move_locks_the_old_pc_and_unlocks_the_new_one()
    {
        var a = await PcStationAsync("PC-01", "pc-01");
        await PcStationAsync("PC-02", "pc-02");
        await using var agentA = await ConnectAsync("pc-01");
        await using var agentB = await ConnectAsync("pc-02");
        var session = await PrepaidAsync(a, 60);
        var target = (await StationsAsync()).Single(s => s.Name == "PC-02");

        Assert.True((await SvcAsync(s => s.MoveAsync(session.Id, target.Id))).Success);

        await WaitForAsync(agentA, 3);
        await WaitForAsync(agentB, 2);
        Assert.Equal(["Lock", "Unlock", "Lock"], Methods(agentA));
        Assert.Contains("PC-02", ((LockInfo)agentA.Calls[2].Args[0]!).Reason);
        Assert.Equal(["Lock", "Unlock"], Methods(agentB));
        Assert.Equal("PC-02", ((SessionInfo)agentB.Calls[1].Args[0]!).StationName);
    }

    [Fact]
    public async Task Moving_a_paused_session_leaves_both_pcs_locked()
    {
        var a = await PcStationAsync("PC-01", "pc-01");
        await PcStationAsync("PC-02", "pc-02");
        await using var agentA = await ConnectAsync("pc-01");
        await using var agentB = await ConnectAsync("pc-02");
        var session = await PrepaidAsync(a, 60);
        await SvcAsync(s => s.PauseAsync(session.Id));
        var target = (await StationsAsync()).Single(s => s.Name == "PC-02");

        Assert.True((await SvcAsync(s => s.MoveAsync(session.Id, target.Id))).Success);

        await WaitForAsync(agentA, 4);
        await WaitForAsync(agentB, 2);
        Assert.Equal(["Lock", "Unlock", "Lock", "Lock"], Methods(agentA));
        Assert.Equal(["Lock", "Lock"], Methods(agentB));
        Assert.Equal("Session paused", ((LockInfo)agentB.Calls[1].Args[0]!).Reason);
    }

    [Fact]
    public async Task Extend_while_paused_sends_nothing_and_extend_in_grace_unlocks_then_updates()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("pc-01");
        var session = await PrepaidAsync(stationId, 30);
        await SvcAsync(s => s.PauseAsync(session.Id));
        Assert.True((await SvcAsync(s => s.ExtendAsync(session.Id, 5))).Success);
        await SvcAsync(s => s.ResumeAsync(session.Id));
        await WaitForAsync(agent, 4);
        Assert.Equal(["Lock", "Unlock", "Lock", "Unlock"], Methods(agent)); // no SessionUpdated while paused

        Advance(35 * Min);
        await EvalAsync();
        await WaitForAsync(agent, 5);
        Assert.Equal("GraceStarted", agent.Calls[4].Method);

        Assert.True((await SvcAsync(s => s.ExtendAsync(session.Id, 10))).Success);
        await WaitForAsync(agent, 7);
        Assert.Equal(["Unlock", "SessionUpdated"], Methods(agent).Skip(5).ToArray());
    }

    [Fact]
    public async Task Warnings_then_grace_reach_the_agent_at_each_threshold()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("pc-01");
        await PrepaidAsync(stationId, 30);

        Advance(20 * Min);
        await EvalAsync();
        Advance(5 * Min);
        await EvalAsync();
        Advance(4 * Min);
        await EvalAsync();
        Advance(1 * Min);
        await EvalAsync();

        await WaitForAsync(agent, 6);
        Assert.Equal(["Lock", "Unlock", "ShowWarning", "ShowWarning", "ShowWarning", "GraceStarted"], Methods(agent));
        Assert.Equal([10, 5, 1], agent.Calls.Skip(2).Take(3).Select(c => (int)c.Args[0]!).ToArray());
        Assert.Equal(120, (int)agent.Calls[5].Args[0]!);
    }

    [Fact]
    public async Task Lock_is_sent_once_when_grace_elapses_and_not_repeated()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("pc-01");
        var session = await PrepaidAsync(stationId, 10);
        Advance(10 * Min);
        await EvalAsync();
        Advance(3 * Min);
        await EvalAsync();
        await EvalAsync();

        await WaitUntilAsync(() => agent.Count("Lock") == 2, "Lock after grace");
        await Task.Delay(300);
        await EvalAsync();
        await Task.Delay(200);

        Assert.Equal(2, agent.Count("Lock"));
        Assert.Equal(SessionStatus.Ended, (await LoadAsync(session.Id)).Status);
        Assert.Equal(PcLockReason, ((LockInfo)agent.Calls.Last(c => c.Method == "Lock").Args[0]!).Reason);
    }

    private const string PcLockReason = Cafe.Server.Agents.PcLockExpiryActionHandler.Reason;

    [Fact]
    public async Task A_late_pass_sends_only_the_smallest_warning()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("pc-01");
        await PrepaidAsync(stationId, 30);

        Advance(26 * Min); // 4 minutes left: the 10 and 5 minute thresholds are both crossed
        await EvalAsync();

        await WaitForAsync(agent, 3);
        await Task.Delay(200);
        Assert.Equal(["Lock", "Unlock", "ShowWarning"], Methods(agent));
        Assert.Equal(5, (int)agent.Calls[2].Args[0]!);
    }

    [Fact]
    public async Task A_late_pass_that_crosses_the_whole_grace_sends_lock_and_no_grace_started()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("pc-01");
        await PrepaidAsync(stationId, 10);

        Advance(30 * Min);
        await EvalAsync();

        await WaitUntilAsync(() => agent.Count("Lock") == 2, "Lock");
        await Task.Delay(200);
        Assert.Equal(["Lock", "Unlock", "Lock"], Methods(agent));
    }

    private async Task<List<Station>> StationsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<StationService>().ListAsync();
    }

    [Fact]
    public async Task Connecting_mid_session_unlocks()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await PrepaidAsync(stationId, 120);

        await using var agent = await ConnectAsync("pc-01");

        await Task.Delay(200);
        Assert.Equal(["Unlock"], Methods(agent));
    }

    [Fact]
    public async Task Connecting_inside_the_warning_window_unlocks_then_warns()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await PrepaidAsync(stationId, 30);
        Advance(26 * Min); // 4 minutes left

        await using var agent = await ConnectAsync("pc-01", 2);

        await Task.Delay(200);
        Assert.Equal(["Unlock", "ShowWarning"], Methods(agent));
        Assert.Equal(5, (int)agent.Calls[1].Args[0]!);
    }

    [Fact]
    public async Task Connecting_during_grace_sends_the_remaining_grace_and_no_unlock()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await PrepaidAsync(stationId, 10);
        Advance(10 * Min + TimeSpan.FromSeconds(30));
        await EvalAsync();

        await using var agent = await ConnectAsync("pc-01");

        await Task.Delay(200);
        Assert.Equal(["GraceStarted"], Methods(agent));
        Assert.Equal(90, (int)agent.Calls[0].Args[0]!);
    }

    [Fact]
    public async Task Connecting_with_a_paused_session_locks_with_the_paused_reason()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        var session = await PrepaidAsync(stationId, 30);
        await SvcAsync(s => s.PauseAsync(session.Id));

        await using var agent = await ConnectAsync("pc-01");

        await Task.Delay(200);
        Assert.Equal(["Lock"], Methods(agent));
        Assert.Equal("Session paused", ((LockInfo)agent.Calls[0].Args[0]!).Reason);
    }

    [Fact]
    public async Task Grace_of_zero_locks_at_expiry_and_never_sends_grace_started()
    {
        await SetSettingsAsync(0, "10,5,1");
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("pc-01");
        await PrepaidAsync(stationId, 10);
        Advance(10 * Min);
        await EvalAsync();

        await WaitUntilAsync(() => agent.Count("Lock") == 2, "Lock at expiry");
        await Task.Delay(200);
        Assert.Equal(["Lock", "Unlock", "Lock"], Methods(agent));
    }

    [Fact]
    public async Task Stations_without_a_connected_agent_or_that_are_consoles_receive_nothing_and_never_throw()
    {
        var offline = await PcStationAsync("PC-01", "pc-01");
        using (var scope = Factory.Services.CreateScope())
        {
            var console = (await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
            {
                Name = "PS5", StationTypeId = await TypeAsync(), Kind = StationKind.Console,
            })).Value!;
            var started = await PrepaidAsync(console.Id, 10);
            await SvcAsync(s => s.PauseAsync(started.Id));
            await SvcAsync(s => s.ResumeAsync(started.Id));
            await SvcAsync(s => s.ExtendAsync(started.Id, 5));
            await SvcAsync(s => s.StopAsync(started.Id));
        }

        var session = await PrepaidAsync(offline, 10);
        await SvcAsync(s => s.PauseAsync(session.Id));
        await SvcAsync(s => s.ResumeAsync(session.Id));
        Advance(30 * Min);
        await EvalAsync();

        Assert.Equal(SessionStatus.Ended, (await LoadAsync(session.Id)).Status);
    }
}
