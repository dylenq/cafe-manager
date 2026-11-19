using Cafe.Server.Agents;
using Cafe.Server.Dashboard;
using Cafe.Server.Data;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class AgentStatusTests : AgentTestBase
{
    private async Task<StationCardModel> CardAsync(int stationId)
    {
        using var scope = Factory.Services.CreateScope();
        var cards = await scope.ServiceProvider.GetRequiredService<DashboardService>().GetCardsAsync();
        return cards.Single(c => c.StationId == stationId);
    }

    [Fact]
    public async Task Pc_with_a_machine_id_is_offline_until_its_agent_connects_and_offline_again_after()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        Assert.Equal(StationStatus.Offline, (await CardAsync(stationId)).Status);

        var agent = new RecordingAgentClient(Factory, await KeyAsync(), "PC-01"); // any case
        await agent.StartAsync();
        await WaitUntilAsync(() => Factory.Services.GetRequiredService<AgentRegistry>().IsConnected("pc-01"), "agent registered");
        Assert.Equal(StationStatus.Idle, (await CardAsync(stationId)).Status);

        await agent.DisposeAsync();
        await WaitUntilAsync(() => !Factory.Services.GetRequiredService<AgentRegistry>().IsConnected("pc-01"), "agent gone");
        Assert.Equal(StationStatus.Offline, (await CardAsync(stationId)).Status);
    }

    [Fact]
    public async Task Pc_without_a_machine_id_stays_idle_and_a_console_with_a_tv_stays_offline()
    {
        var pc = await PcStationAsync("PC-01", null);
        int console;
        using (var scope = Factory.Services.CreateScope())
        {
            console = (await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
            {
                Name = "PS5", StationTypeId = await TypeAsync(), Kind = StationKind.Console, TvIpAddress = "192.168.1.50",
            })).Value!.Id;
        }

        Assert.Equal(StationStatus.Idle, (await CardAsync(pc)).Status);
        Assert.False((await CardAsync(pc)).AgentOffline);
        Assert.Equal(StationStatus.Offline, (await CardAsync(console)).Status);
        Assert.False((await CardAsync(console)).AgentOffline);
    }

    [Fact]
    public async Task A_session_overrides_the_status_and_the_card_flags_a_disconnected_agent()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await PrepaidAsync(stationId, 60);

        var offline = await CardAsync(stationId);
        Assert.Equal(StationStatus.InSession, offline.Status);
        Assert.True(offline.AgentOffline);

        await using var agent = new RecordingAgentClient(Factory, await KeyAsync(), "pc-01");
        await agent.StartAsync();
        await WaitUntilAsync(() => Factory.Services.GetRequiredService<AgentRegistry>().IsConnected("pc-01"), "agent registered");

        var online = await CardAsync(stationId);
        Assert.Equal(StationStatus.InSession, online.Status);
        Assert.False(online.AgentOffline);
    }

    [Fact]
    public async Task Registry_changed_fires_on_connect_and_disconnect_and_an_unknown_id_changes_no_card()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        var seen = new List<string>();
        var registry = Factory.Services.GetRequiredService<AgentRegistry>();
        void Handler(string id)
        {
            lock (seen)
            {
                seen.Add(id);
            }
        }

        registry.Changed += Handler;
        try
        {
            var agent = new RecordingAgentClient(Factory, await KeyAsync(), "mystery-pc");
            await agent.StartAsync();
            await WaitUntilAsync(() => { lock (seen) { return seen.Count >= 1; } }, "connect event");
            Assert.Equal(StationStatus.Offline, (await CardAsync(stationId)).Status);

            await agent.DisposeAsync();
            await WaitUntilAsync(() => { lock (seen) { return seen.Count >= 2; } }, "disconnect event");
            Assert.All(seen, id => Assert.Equal("mystery-pc", id));
        }
        finally
        {
            registry.Changed -= Handler;
        }
    }

    [Fact]
    public void Parameterless_default_provider_keeps_the_phase_1_rule()
    {
        var provider = new DefaultStationStatusProvider();

        Assert.Equal(StationStatus.Offline, provider.GetStatus(new Station { Kind = StationKind.PC, AgentMachineId = "pc-01" }));
        Assert.Equal(StationStatus.Offline, provider.GetStatus(new Station { Kind = StationKind.Console, TvIpAddress = "10.0.0.2" }));
        Assert.Equal(StationStatus.Idle, provider.GetStatus(new Station { Kind = StationKind.PC }));
    }
}
