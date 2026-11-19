using Cafe.Agent.Core;
using Cafe.Server.Agents;
using Cafe.Server.Sessions;
using Cafe.Shared.Agents;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>WR-02 / IN-02: per-machine ordering, state-driven commands and sequence numbers.</summary>
public class AgentCommandOrderingTests : AgentTestBase
{
    private async Task<RecordingAgentClient> ConnectAsync(string machineId)
    {
        var recorder = new RecordingAgentClient(Factory, await KeyAsync(), machineId);
        await recorder.StartAsync();
        await WaitUntilAsync(() => recorder.Calls.Count >= 1, "initial push");
        return recorder;
    }

    private static long SequenceOf(object? message) => message switch
    {
        LockInfo l => l.Sequence,
        SessionInfo s => s.Sequence,
        _ => throw new InvalidOperationException("Not a sequenced message."),
    };

    [Fact]
    public async Task State_changing_messages_carry_a_strictly_increasing_sequence()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("pc-01");
        var session = await PrepaidAsync(stationId, 60);
        Assert.True((await SvcAsync(s => s.PauseAsync(session.Id))).Success);
        Assert.True((await SvcAsync(s => s.ResumeAsync(session.Id))).Success);
        Assert.True((await SvcAsync(s => s.StopAsync(session.Id))).Success);
        await WaitUntilAsync(() => agent.Calls.Count >= 5, "five messages");

        var sequences = agent.Calls.Select(c => SequenceOf(c.Args[0])).ToList();
        Assert.All(sequences, s => Assert.True(s > 0));
        Assert.Equal(sequences.OrderBy(s => s), sequences);
        Assert.Equal(sequences.Count, sequences.Distinct().Count());
    }

    [Fact]
    public async Task Stale_pause_signals_racing_a_running_session_never_lock_the_pc()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("pc-01");
        var session = await PrepaidAsync(stationId, 60);
        await WaitUntilAsync(() => agent.Count("Unlock") == 1, "unlock after start");
        var commands = Factory.Services.GetRequiredService<AgentCommandService>();

        // Late Paused/Stopped signals from earlier actions arrive while the session is Active again.
        var signals = Enumerable.Range(0, 10).SelectMany(_ => new[]
        {
            new SessionSignal(SessionSignalKind.Paused, stationId, session.Id),
            new SessionSignal(SessionSignalKind.Resumed, stationId, session.Id),
        });
        await Task.WhenAll(signals.Select(s => Task.Run(() => commands.OnSessionSignalAsync(s, CancellationToken.None))));

        await WaitUntilAsync(() => agent.Count("Unlock") == 21, "an Unlock per signal");
        Assert.Equal(1, agent.Count("Lock")); // only the initial lock before the session
        Assert.Equal("Unlock", agent.Calls[^1].Method);
    }

    [Fact]
    public async Task Concurrent_pushes_for_one_machine_are_serialized_with_increasing_sequences()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("pc-01");
        await PrepaidAsync(stationId, 60);
        await WaitUntilAsync(() => agent.Count("Unlock") == 1, "unlock after start");
        var commands = Factory.Services.GetRequiredService<AgentCommandService>();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => commands.PushStateAsync("pc-01")));
        await WaitUntilAsync(() => agent.Count("Unlock") >= 9, "nine unlocks");

        var sequences = agent.Calls.Where(c => c.Method == "Unlock").Select(c => SequenceOf(c.Args[0])).ToList();
        Assert.Equal(sequences.OrderBy(s => s), sequences);
    }

    [Fact]
    public async Task Agent_ignores_a_stale_lock_or_update_and_resets_on_reconnect()
    {
        var a = new UnitAgent();
        await a.Controller.Unlock(new SessionInfo(AgentSessionMode.Prepaid, 600, 0, "PC-01", a.Clock.UtcNow, Sequence: 10));
        await a.Controller.Lock(new LockInfo("PC-01", "Cafe", "newer", Sequence: 12));
        Assert.Equal(AgentState.Locked, a.Controller.State);

        // A late Unlock (sequence 11) must not undo the newer Lock.
        await a.Controller.Unlock(new SessionInfo(AgentSessionMode.Prepaid, 600, 0, "PC-01", a.Clock.UtcNow, Sequence: 11));
        Assert.Equal(AgentState.Locked, a.Controller.State);

        // A newer Unlock is applied; a stale Lock afterwards is not.
        await a.Controller.Unlock(new SessionInfo(AgentSessionMode.Prepaid, 600, 0, "PC-01", a.Clock.UtcNow, Sequence: 13));
        Assert.Equal(AgentState.Unlocked, a.Controller.State);
        await a.Controller.Lock(new LockInfo("PC-01", "Cafe", "stale", Sequence: 12));
        Assert.Equal(AgentState.Unlocked, a.Controller.State);

        // Unsequenced messages (sequence 0) are always applied.
        await a.Controller.Lock(new LockInfo("PC-01", "Cafe", "manual"));
        Assert.Equal(AgentState.Locked, a.Controller.State);

        // After a reconnect the counter starts over (the server pushes the current state).
        a.Controller.HandleConnected();
        a.Controller.HandleDisconnected();
        await a.Controller.Unlock(new SessionInfo(AgentSessionMode.Prepaid, 600, 0, "PC-01", a.Clock.UtcNow, Sequence: 1));
        Assert.Equal(AgentState.Unlocked, a.Controller.State);
    }
}
