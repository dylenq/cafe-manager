using Cafe.Agent.Core;
using Cafe.Server.Agents;
using Cafe.Shared.Agents;
using Cafe.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cafe.Tests;

public class AgentConnectionResilienceTests : AgentTestBase
{
    [Fact]
    public void Backoff_is_0_2_5_10_30_then_30_forever_and_never_null()
    {
        var expected = new[] { 0, 2, 5, 10, 30, 30, 30 };
        for (var attempt = 0; attempt < expected.Length; attempt++)
        {
            Assert.Equal(TimeSpan.FromSeconds(expected[attempt]), AgentRetryPolicy.DelayFor(attempt));
        }

        var policy = new AgentRetryPolicy();
        foreach (var count in new long[] { 0, 1, 4, 5, 100, 100_000 })
        {
            var delay = policy.NextRetryDelay(new Microsoft.AspNetCore.SignalR.Client.RetryContext
            {
                PreviousRetryCount = count,
                ElapsedTime = TimeSpan.FromDays(3),
                RetryReason = new Exception("x"),
            });
            Assert.NotNull(delay);
        }
    }

    [Fact]
    public async Task Unreachable_server_keeps_the_agent_locked_and_dispose_stops_cleanly()
    {
        var a = new UnitAgent("http://127.0.0.1:1");
        await a.Controller.StartAsync();
        await Task.Delay(400);

        Assert.Equal(AgentState.Locked, a.Controller.State);
        Assert.False(a.Controller.IsConnected);

        var dispose = a.Controller.DisposeAsync().AsTask();
        Assert.Same(dispose, await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public async Task Wrong_key_keeps_retrying_locked_and_a_corrected_key_connects()
    {
        await PcStationAsync("PC-01", "pc-01");
        await using var bad = await StartAgentAsync("pc-01", key: "definitely-wrong");
        await Task.Delay(500);
        Assert.Equal(AgentState.Locked, bad.Controller.State);
        Assert.False(bad.Controller.IsConnected);

        await using var good = await StartAgentAsync("pc-01");
        await WaitUntilAsync(() => good.Controller.IsConnected, "corrected key connects");
        Assert.False(bad.Controller.IsConnected);
    }

    private async Task<TestAgent> StartSlowReconnectAgentAsync(string machineId)
    {
        var lockScreen = new FakeLockScreen();
        var options = new AgentConnectionOptions
        {
            ServerUrl = "http://localhost",
            SharedKey = await KeyAsync(),
            MachineId = machineId,
            ReconnectDelays = [TimeSpan.FromSeconds(1)],
            ConfigureHttp = o =>
            {
                o.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            },
        };
        var controller = new AgentController(options, lockScreen, new FakeWarningDisplay(), new FakePowerControl(),
            new SystemAgentClock(), NullLogger<AgentController>.Instance);
        await controller.StartAsync();
        return new TestAgent(controller, lockScreen);
    }

    [Fact]
    public async Task Dropped_unlocked_agent_keeps_counting_then_reconnects_and_the_server_state_is_replaced()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await StartSlowReconnectAgentAsync("pc-01");
        var drops = 0;
        agent.Controller.ConnectedChanged += connected =>
        {
            if (!connected)
            {
                Interlocked.Increment(ref drops);
            }
        };
        await WaitUntilAsync(() => agent.Controller.IsConnected, "connected");
        await PrepaidAsync(stationId, 60);
        await WaitUntilAsync(() => agent.Controller.State == AgentState.Unlocked, "unlocked");

        Factory.Services.GetRequiredService<AgentRegistry>().DisconnectAll();

        await WaitUntilAsync(() => Volatile.Read(ref drops) >= 1, "drop noticed");
        Assert.Equal(AgentState.Unlocked, agent.Controller.State);
        Assert.True(agent.Controller.RemainingSeconds is > 0);

        await WaitUntilAsync(() => agent.Controller.IsConnected, "reconnected");
        await WaitUntilAsync(() => agent.Controller.State == AgentState.Unlocked && agent.Controller.RemainingSeconds is > 0, "state re-pushed");
    }

    [Fact]
    public async Task Session_stopped_during_the_gap_locks_the_agent_after_reconnect()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await StartSlowReconnectAgentAsync("pc-01");
        await WaitUntilAsync(() => agent.Controller.IsConnected, "connected");
        var session = await PrepaidAsync(stationId, 60);
        await WaitUntilAsync(() => agent.Controller.State == AgentState.Unlocked, "unlocked");

        Factory.Services.GetRequiredService<AgentRegistry>().DisconnectAll();
        var stopped = await SvcAsync(s => s.StopAsync(session.Id));
        Assert.True(stopped.Success, stopped.Error);

        await WaitUntilAsync(() => agent.Controller.State == AgentState.Locked, "locked after reconnect push");
    }
}
