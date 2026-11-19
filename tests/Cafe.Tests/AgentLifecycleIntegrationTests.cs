using System.Net;
using Cafe.Agent.Core;
using Cafe.Server.Agents;
using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Shared.Agents;
using Cafe.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cafe.Tests;

/// <summary>
/// End-to-end regression net for AGNT-01 to AGNT-06: real AgentController instances (fake devices) against the real
/// server through the TestServer. The agent clock is the server's fake clock, so one Advance moves both sides.
/// </summary>
public class AgentLifecycleIntegrationTests : AgentTestBase
{
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    private sealed class ServerClockAdapter(TimeProvider time) : IAgentClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();

        public long MonotonicMilliseconds => time.GetUtcNow().ToUnixTimeMilliseconds();
    }

    private sealed class Rig(AgentController controller, RecordingLockScreen lockScreen, RecordingWarningDisplay display, RecordingPowerControl power) : IAsyncDisposable
    {
        public AgentController Controller { get; } = controller;

        public RecordingLockScreen Lock { get; } = lockScreen;

        public RecordingWarningDisplay Display { get; } = display;

        public RecordingPowerControl Power { get; } = power;

        public string[] Messages => Display.Messages.ToArray();

        public ValueTask DisposeAsync() => Controller.DisposeAsync();
    }

    private async Task<Rig> StartRigAsync(string machineId, string? key = null, TimeSpan[]? delays = null)
    {
        var lockScreen = new RecordingLockScreen();
        var display = new RecordingWarningDisplay();
        var power = new RecordingPowerControl();
        var controller = new AgentController(
            new AgentConnectionOptions
            {
                ServerUrl = "http://localhost",
                SharedKey = key ?? await KeyAsync(),
                MachineId = machineId,
                ReconnectDelays = delays ?? [TimeSpan.Zero, TimeSpan.FromSeconds(1)],
                ConfigureHttp = o =>
                {
                    o.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                    o.Transports = HttpTransportType.LongPolling;
                },
            },
            lockScreen, display, power, new ServerClockAdapter(Factory.Clock), NullLogger<AgentController>.Instance);
        await controller.StartAsync();
        return new Rig(controller, lockScreen, display, power);
    }

    private Task EvalAsync() => Factory.Services.GetRequiredService<IExpiryEvaluator>().EvaluateAsync(CancellationToken.None);

    private static Task WaitStateAsync(Rig rig, AgentState state) =>
        WaitUntilAsync(() => rig.Controller.State == state, $"state {state} (is {rig.Controller.State})");

    private async Task<Rig> ConnectedLockedRigAsync(string machineId)
    {
        var rig = await StartRigAsync(machineId);
        await WaitUntilAsync(() => rig.Controller.IsConnected && rig.Lock.ShowCount >= 1, $"{machineId} connected and locked");
        Assert.Equal(AgentState.Locked, rig.Controller.State);
        return rig;
    }

    private async Task<RecordingAgentClient> RawAsync(string machineId)
    {
        var raw = new RecordingAgentClient(Factory, await KeyAsync(), machineId);
        await raw.StartAsync();
        return raw;
    }

    [Fact]
    public async Task Prepaid_session_runs_through_warnings_grace_and_lock_after_grace()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var rig = await ConnectedLockedRigAsync("pc-01");

        var session = await PrepaidAsync(stationId, 60);
        await WaitStateAsync(rig, AgentState.Unlocked);
        Assert.Equal(3600, rig.Controller.RemainingSeconds);

        Advance(50 * Min);
        await EvalAsync();
        await WaitUntilAsync(() => rig.Messages.Length == 1, "10 minute warning");
        Advance(5 * Min);
        await EvalAsync();
        await WaitUntilAsync(() => rig.Messages.Length == 2, "5 minute warning");
        Advance(4 * Min);
        await EvalAsync();
        await WaitUntilAsync(() => rig.Messages.Length == 3, "1 minute warning");
        Assert.Equal(["10 minutes remaining", "5 minutes remaining", "1 minute remaining"], rig.Messages);

        Advance(1 * Min);
        await EvalAsync();
        await WaitStateAsync(rig, AgentState.Grace);
        rig.Controller.Tick();
        Assert.True(rig.Display.Countdowns.Last().IsGrace);
        Assert.Equal(120, rig.Controller.GraceRemainingSeconds);

        Advance(3 * Min);
        await EvalAsync();
        await WaitStateAsync(rig, AgentState.Locked);
        var ended = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Ended, ended.Status);
        Assert.Contains(ended.Events, e => e.Type == SessionEventType.ExpiryActionFired);
    }

    [Fact]
    public async Task Pause_resume_extend_and_stop_drive_the_agent()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var rig = await ConnectedLockedRigAsync("pc-01");
        var session = await PrepaidAsync(stationId, 60);
        await WaitStateAsync(rig, AgentState.Unlocked);

        Assert.True((await SvcAsync(s => s.PauseAsync(session.Id))).Success);
        await WaitStateAsync(rig, AgentState.Locked);
        Assert.True((await SvcAsync(s => s.ResumeAsync(session.Id))).Success);
        await WaitStateAsync(rig, AgentState.Unlocked);

        Assert.True((await SvcAsync(s => s.ExtendAsync(session.Id, 30))).Success);
        await WaitUntilAsync(() => rig.Controller.RemainingSeconds == 90 * 60, "extended by 30 minutes");

        Assert.True((await SvcAsync(s => s.StopAsync(session.Id))).Success);
        await WaitStateAsync(rig, AgentState.Locked);
    }

    [Fact]
    public async Task Extending_out_of_grace_returns_the_agent_to_unlocked()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var rig = await ConnectedLockedRigAsync("pc-01");
        var session = await PrepaidAsync(stationId, 30);
        await WaitStateAsync(rig, AgentState.Unlocked);

        Advance(30 * Min);
        await EvalAsync();
        await WaitStateAsync(rig, AgentState.Grace);

        Assert.True((await SvcAsync(s => s.ExtendAsync(session.Id, 15))).Success);

        await WaitStateAsync(rig, AgentState.Unlocked);
        await WaitUntilAsync(() => rig.Controller.RemainingSeconds == 15 * 60, "15 minutes after grace extend");
    }

    [Fact]
    public async Task Moving_a_session_locks_the_first_pc_and_unlocks_the_second()
    {
        var a = await PcStationAsync("PC-01", "pc-01");
        var b = await PcStationAsync("PC-02", "pc-02");
        await using var rigA = await ConnectedLockedRigAsync("pc-01");
        await using var rigB = await ConnectedLockedRigAsync("pc-02");
        var session = await PrepaidAsync(a, 60);
        await WaitStateAsync(rigA, AgentState.Unlocked);

        Assert.True((await SvcAsync(s => s.MoveAsync(session.Id, b))).Success);

        await WaitStateAsync(rigA, AgentState.Locked);
        await WaitStateAsync(rigB, AgentState.Unlocked);
        Assert.Contains("PC-02", rigA.Lock.LastLock!.Reason);
    }

    [Fact]
    public async Task Manual_commands_reach_the_agent_and_are_audited()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var rig = await ConnectedLockedRigAsync("pc-01");
        var remote = Factory.Services.GetRequiredService<AgentRemoteControlService>();

        Assert.True((await remote.SendAsync(stationId, AgentCommandKind.Unlock)).Success);
        await WaitStateAsync(rig, AgentState.Unlocked);
        Assert.Null(rig.Controller.RemainingSeconds);

        Assert.True((await remote.SendAsync(stationId, AgentCommandKind.Message, "Closing in 10")).Success);
        await WaitUntilAsync(() => rig.Messages.Contains("Closing in 10"), "manual message on the agent display");

        Assert.True((await remote.SendAsync(stationId, AgentCommandKind.Lock)).Success);
        await WaitStateAsync(rig, AgentState.Locked);

        Assert.True((await remote.SendAsync(stationId, AgentCommandKind.Shutdown)).Success);
        await WaitUntilAsync(() => rig.Power.ShutdownCount == 1, "shutdown called once");
        Assert.Equal(0, rig.Power.RestartCount);

        var logs = await Factory.Services.GetRequiredService<AgentCommandLogService>().ListAsync();
        Assert.Equal([AgentCommandKind.Shutdown, AgentCommandKind.Lock, AgentCommandKind.Message, AgentCommandKind.Unlock],
            logs.Select(l => l.Command).ToArray());
    }

    [Fact]
    public async Task Server_side_drop_keeps_the_agent_unlocked_then_reconnect_applies_the_server_state()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var rig = await StartRigAsync("pc-01", delays: [TimeSpan.FromSeconds(1)]);
        await WaitUntilAsync(() => rig.Controller.IsConnected, "connected");
        var session = await PrepaidAsync(stationId, 60);
        await WaitStateAsync(rig, AgentState.Unlocked);
        var drops = 0;
        rig.Controller.ConnectedChanged += connected =>
        {
            if (!connected)
            {
                Interlocked.Increment(ref drops);
            }
        };

        Factory.Services.GetRequiredService<AgentRegistry>().DisconnectAll();
        await WaitUntilAsync(() => Volatile.Read(ref drops) >= 1, "drop noticed");

        // The agent stays unlocked and keeps counting while disconnected.
        Assert.Equal(AgentState.Unlocked, rig.Controller.State);
        Advance(5 * Min);
        rig.Controller.Tick();
        Assert.Equal(AgentState.Unlocked, rig.Controller.State);
        Assert.Equal(55 * 60, rig.Controller.RemainingSeconds);

        // Staff stop the session during the gap: the reconnect re-push locks the PC.
        var stop = await SvcAsync(s => s.StopAsync(session.Id));
        Assert.True(stop.Success, stop.Error);
        await WaitUntilAsync(() => rig.Controller.IsConnected, "reconnected");
        await WaitStateAsync(rig, AgentState.Locked);
    }

    [Fact]
    public async Task Reconnect_after_a_drop_re_pushes_the_running_session()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var rig = await StartRigAsync("pc-01", delays: [TimeSpan.FromSeconds(1)]);
        await WaitUntilAsync(() => rig.Controller.IsConnected, "connected");
        await PrepaidAsync(stationId, 60);
        await WaitStateAsync(rig, AgentState.Unlocked);
        var disconnected = false;
        rig.Controller.ConnectedChanged += connected => disconnected |= !connected;

        Factory.Services.GetRequiredService<AgentRegistry>().DisconnectAll();
        await WaitUntilAsync(() => disconnected, "drop noticed");
        Advance(10 * Min);
        await WaitUntilAsync(() => rig.Controller.IsConnected, "reconnected");

        await WaitUntilAsync(() => rig.Controller.RemainingSeconds == 50 * 60, "server state re-pushed after reconnect");
        Assert.Equal(AgentState.Unlocked, rig.Controller.State);
    }

    [Fact]
    public async Task Bad_key_keeps_the_agent_locked_and_negotiate_is_401()
    {
        await PcStationAsync("PC-01", "pc-01");
        await using var rig = await StartRigAsync("pc-01", key: "not-the-key");
        await Task.Delay(400);

        Assert.Equal(AgentState.Locked, rig.Controller.State);
        Assert.False(rig.Controller.IsConnected);
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, AgentProtocol.HubPath + "/negotiate?negotiateVersion=1");
        request.Headers.TryAddWithoutValidation(AgentProtocol.KeyHeader, "not-the-key");
        request.Headers.TryAddWithoutValidation(AgentProtocol.MachineIdHeader, "pc-01");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Key_regeneration_disconnects_the_agent_until_its_key_is_updated()
    {
        await PcStationAsync("PC-01", "pc-01");
        var settings = Factory.Services.GetRequiredService<AgentSettingsService>();
        await using var oldRig = await ConnectedLockedRigAsync("pc-01");

        var newKey = await settings.RegenerateSharedKeyAsync();

        await WaitUntilAsync(() => !oldRig.Controller.IsConnected, "agent dropped by regeneration");
        await Task.Delay(1500); // reconnect attempts with the old key keep failing
        Assert.False(oldRig.Controller.IsConnected);
        Assert.Equal(AgentState.Locked, oldRig.Controller.State);

        await using var updated = await StartRigAsync("pc-01", key: newKey);
        await WaitUntilAsync(() => updated.Controller.IsConnected, "agent with the new key connects");
    }

    [Fact]
    public async Task Unknown_machine_id_connects_locked_and_is_listed_as_unregistered()
    {
        await PcStationAsync("PC-01", "pc-01");
        await using var rig = await ConnectedLockedRigAsync("Mystery-PC");
        var admin = Factory.Services.GetRequiredService<AgentAdminService>();

        await WaitUntilAsync(() => admin.GetUnregisteredAsync().Result.Count == 1, "unregistered listed");

        Assert.Equal("Mystery-PC", (await admin.GetUnregisteredAsync())[0].MachineId);
        Assert.Equal(AgentState.Locked, rig.Controller.State);
        await WaitUntilAsync(() => rig.Lock.LastLock?.Reason == AgentCommandService.UnknownMachineReason, "unknown machine lock reason");
    }

    [Fact]
    public async Task Sessions_page_shows_the_agent_commands_tab_newest_first_and_html_encoded()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var rig = await ConnectedLockedRigAsync("pc-01");
        var remote = Factory.Services.GetRequiredService<AgentRemoteControlService>();
        await remote.SendAsync(stationId, AgentCommandKind.Lock);
        Advance(Min);
        await remote.SendAsync(stationId, AgentCommandKind.Message, "<b>hi</b>");

        var sessionsTab = await client.GetStringAsync("/sessions");
        var commandsTab = await client.GetStringAsync("/sessions?tab=commands");

        Assert.Contains("Agent commands", sessionsTab);
        Assert.Contains(">Sessions<", sessionsTab);
        Assert.DoesNotContain("agent-commands-table", sessionsTab);
        Assert.Contains("agent-commands-table", commandsTab);
        Assert.DoesNotContain("<b>hi</b>", commandsTab);
        Assert.Contains("&lt;b&gt;hi&lt;/b&gt;", commandsTab);
        Assert.True(commandsTab.IndexOf("Message", StringComparison.Ordinal) < commandsTab.LastIndexOf(">Lock<", StringComparison.Ordinal));
        Assert.Contains("PC-01", commandsTab);

        var anonymous = Factory.CreateNoRedirectClient();
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/sessions?tab=commands")).StatusCode);
    }
}
