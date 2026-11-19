using Cafe.Agent.Core;
using Cafe.Server.Agents;
using Cafe.Server.Sessions;
using Cafe.Shared.Agents;
using Cafe.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class AgentTracerTests : AgentTestBase
{
    [Fact]
    public async Task Agent_is_locked_on_connect_unlocked_on_start_and_locked_on_stop()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await StartAgentAsync("PC-01");
        await WaitUntilAsync(() => agent.Controller.IsConnected && agent.Controller.State == AgentState.Locked, "agent connected");
        await WaitUntilAsync(() => agent.LockScreen.ShowCount >= 1, "lock screen shown");

        var session = await PrepaidAsync(stationId, 60);

        await WaitUntilAsync(() => agent.Controller.State == AgentState.Unlocked, "Unlock after start");
        Assert.True(agent.LockScreen.HideCount >= 1);

        var shownBeforeStop = agent.LockScreen.ShowCount;
        var stopped = await SvcAsync(s => s.StopAsync(session.Id));
        Assert.True(stopped.Success, stopped.Error);

        await WaitUntilAsync(() => agent.Controller.State == AgentState.Locked, "Lock after stop");
        Assert.True(agent.LockScreen.ShowCount > shownBeforeStop);
    }

    [Fact]
    public async Task Unlock_carries_remaining_seconds_mode_and_station_name()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var recorder = new RecordingAgentClient(Factory, await KeyAsync(), "PC-01");
        await recorder.StartAsync();
        await WaitUntilAsync(() => recorder.Count("Lock") == 1, "Lock on connect");
        var lockInfo = (LockInfo)recorder.Calls[0].Args[0]!;
        Assert.Equal("PC-01", lockInfo.StationName);

        await PrepaidAsync(stationId, 60);

        await WaitUntilAsync(() => recorder.Count("Unlock") == 1, "recorder Unlock");
        var unlock = (SessionInfo)recorder.Calls.Single(c => c.Method == "Unlock").Args[0]!;
        Assert.Equal(3600, unlock.RemainingSeconds);
        Assert.Equal(AgentSessionMode.Prepaid, unlock.Mode);
        Assert.Equal("PC-01", unlock.StationName);
    }

    [Fact]
    public async Task Connecting_while_a_session_is_active_pushes_unlock_immediately()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await PrepaidAsync(stationId, 30);

        await using var agent = await StartAgentAsync("pc-01");

        await WaitUntilAsync(() => agent.Controller.State == AgentState.Unlocked, "Unlock on connect");
    }

    [Fact]
    public async Task Unknown_machine_id_connects_and_is_told_to_lock()
    {
        await using var recorder = new RecordingAgentClient(Factory, await KeyAsync(), "mystery-pc");
        await recorder.StartAsync();

        await WaitUntilAsync(() => recorder.Count("Lock") == 1, "Lock for unknown machine");
        var info = (LockInfo)recorder.Calls[0].Args[0]!;
        Assert.Equal(AgentCommandService.UnknownMachineReason, info.Reason);
    }

    private sealed class ThrowingSink : ISessionEventSink
    {
        public Task OnSessionSignalAsync(SessionSignal signal, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task A_throwing_sink_never_fails_the_session_mutation()
    {
        var stationId = await PcStationAsync();
        var publisher = new SessionSignalPublisher(
            [new ThrowingSink()], Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionSignalPublisher>.Instance);

        await publisher.PublishAsync([new SessionSignal(SessionSignalKind.Started, stationId, 1)]);

        // And through the real service with an extra throwing sink registered in the host.
        using var factory = new FaultySinkFactory();
        using var scope = factory.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<Cafe.Server.Stations.StationTypeService>();
        var type = (await types.CreateAsync(new Cafe.Server.Stations.StationTypeInput { Name = "Std", HourlyRate = 6000m })).Value!;
        var station = (await scope.ServiceProvider.GetRequiredService<Cafe.Server.Stations.StationService>().CreateAsync(
            new Cafe.Server.Stations.StationInput { Name = "PC-X", StationTypeId = type.Id, Kind = Cafe.Shared.StationKind.PC })).Value!;
        var started = await scope.ServiceProvider.GetRequiredService<SessionService>().StartPrepaidAsync(station.Id, 30);

        Assert.True(started.Success, started.Error);
    }

    private sealed class FaultySinkFactory : CafeWebApplicationFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<ISessionEventSink, ThrowingSink>());
        }
    }
}
