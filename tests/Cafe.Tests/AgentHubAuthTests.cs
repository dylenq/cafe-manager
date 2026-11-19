using System.Net;
using Cafe.Agent.Core;
using Cafe.Server.Agents;
using Cafe.Shared.Agents;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class AgentHubAuthTests : AgentTestBase
{
    private const string NegotiatePath = AgentProtocol.HubPath + "/negotiate?negotiateVersion=1";

    private async Task<HttpStatusCode> NegotiateAsync(string? key = null, string? machineId = "pc-01", string query = "")
    {
        using var client = Factory.CreateNoRedirectClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, NegotiatePath + query);
        if (key is not null)
        {
            request.Headers.Add(AgentProtocol.KeyHeader, key);
        }

        if (machineId is not null)
        {
            request.Headers.TryAddWithoutValidation(AgentProtocol.MachineIdHeader, machineId);
        }

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task Missing_or_wrong_key_is_401_and_the_right_key_works_in_header_and_query()
    {
        var key = await KeyAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, await NegotiateAsync(key: null));
        Assert.Equal(HttpStatusCode.Unauthorized, await NegotiateAsync(key: "wrong"));
        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync(key: key));
        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync(key: null, query: "&access_token=" + Uri.EscapeDataString(key)));
    }

    [Fact]
    public async Task Invalid_machine_ids_are_rejected_with_400()
    {
        var key = await KeyAsync();

        Assert.Equal(HttpStatusCode.BadRequest, await NegotiateAsync(key, machineId: null));
        Assert.Equal(HttpStatusCode.BadRequest, await NegotiateAsync(key, machineId: new string('a', 101)));
        Assert.Equal(HttpStatusCode.BadRequest, await NegotiateAsync(key, machineId: "pc\u0001x"));
        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync(key, machineId: new string('a', 100)));
    }

    [Fact]
    public async Task Hub_is_reachable_without_cookie_and_before_admin_setup()
    {
        // No admin password exists in this fresh factory: every other path would redirect to /setup.
        var key = await KeyAsync();

        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync(key));
    }

    [Fact]
    public async Task Connected_machine_matching_no_station_is_unregistered_until_a_station_uses_its_id()
    {
        await using var recorder = new RecordingAgentClient(Factory, await KeyAsync(), "Mystery-PC");
        await recorder.StartAsync();
        var admin = Factory.Services.GetRequiredService<AgentAdminService>();

        await WaitUntilAsync(() => admin.GetUnregisteredAsync().Result.Count == 1, "unregistered listed");
        Assert.Equal("Mystery-PC", (await admin.GetUnregisteredAsync())[0].MachineId);

        await PcStationAsync("PC-09", "mystery-pc");

        Assert.Empty(await admin.GetUnregisteredAsync());
        var connected = Assert.Single(await admin.GetConnectedAsync());
        Assert.Equal("PC-09", connected.StationName);
    }

    [Fact]
    public async Task Machine_id_matching_is_case_insensitive()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await StartAgentAsync("PC-01");
        await WaitUntilAsync(() => agent.Controller.IsConnected, "connected");

        await PrepaidAsync(stationId, 30);

        await WaitUntilAsync(() => agent.Controller.State == AgentState.Unlocked, "Unlock for case-different id");
    }

    [Fact]
    public async Task Regenerating_the_key_disconnects_agents_and_invalidates_the_old_key()
    {
        var settings = Factory.Services.GetRequiredService<AgentSettingsService>();
        var oldKey = await KeyAsync();
        await using var recorder = new RecordingAgentClient(Factory, oldKey, "pc-01");
        await recorder.StartAsync();
        var registry = Factory.Services.GetRequiredService<AgentRegistry>();
        await WaitUntilAsync(() => registry.IsConnected("pc-01"), "connected");

        var newKey = await settings.RegenerateSharedKeyAsync();

        Assert.NotEqual(oldKey, newKey);
        Assert.Equal(newKey, await settings.GetOrCreateSharedKeyAsync());
        await WaitUntilAsync(() => recorder.IsClosed && !registry.IsConnected("pc-01"), "agent disconnected");
        Assert.Equal(HttpStatusCode.Unauthorized, await NegotiateAsync(oldKey));
        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync(newKey));
    }

    [Fact]
    public async Task Newest_connection_with_the_same_machine_id_wins()
    {
        var key = await KeyAsync();
        var registry = Factory.Services.GetRequiredService<AgentRegistry>();
        await using var first = new RecordingAgentClient(Factory, key, "pc-01");
        await first.StartAsync();
        await WaitUntilAsync(() => registry.IsConnected("pc-01"), "first connected");
        var firstId = registry.Snapshot().Single().ConnectionId;

        await using var second = new RecordingAgentClient(Factory, key, "PC-01");
        await second.StartAsync();

        await WaitUntilAsync(() => first.IsClosed, "first aborted");
        var current = Assert.Single(registry.Snapshot());
        Assert.NotEqual(firstId, current.ConnectionId);
        // The late disconnect of the replaced connection must not have removed the newest entry.
        await Task.Delay(200);
        Assert.True(registry.IsConnected("pc-01"));
        Assert.Equal(current.ConnectionId, Assert.Single(registry.Snapshot()).ConnectionId);
    }

    [Fact]
    public async Task Registry_changed_fires_on_connect_and_disconnect()
    {
        var registry = Factory.Services.GetRequiredService<AgentRegistry>();
        var events = new List<string>();
        registry.Changed += id =>
        {
            lock (events)
            {
                events.Add(id);
            }
        };
        var recorder = new RecordingAgentClient(Factory, await KeyAsync(), "pc-07");
        await recorder.StartAsync();
        await WaitUntilAsync(() => { lock (events) { return events.Count == 1; } }, "connect event");

        await recorder.DisposeAsync();

        await WaitUntilAsync(() => { lock (events) { return events.Count == 2; } }, "disconnect event");
        lock (events)
        {
            Assert.All(events, id => Assert.Equal("pc-07", id));
        }
    }
}
