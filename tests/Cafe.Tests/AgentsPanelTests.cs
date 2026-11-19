using System.Net;
using Cafe.Server.Agents;
using Cafe.Server.Data;
using Cafe.Shared;
using Cafe.Shared.Agents;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class AgentsPanelTests : AgentTestBase
{
    private AgentAdminService Admin => Factory.Services.GetRequiredService<AgentAdminService>();

    private async Task<RecordingAgentClient> ConnectAsync(string machineId)
    {
        var recorder = new RecordingAgentClient(Factory, await KeyAsync(), machineId);
        await recorder.StartAsync();
        await WaitUntilAsync(() => recorder.Count("Lock") + recorder.Count("Unlock") >= 1, $"initial push for {machineId}");
        return recorder;
    }

    [Fact]
    public async Task Settings_page_shows_the_pc_agents_section_with_the_key_and_anonymous_is_redirected()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        var key = await KeyAsync();

        var response = await client.GetAsync("/settings");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("PC agents", body);
        Assert.Contains(key, body);
        Assert.Contains(AgentProtocol.HubPath, body);

        var anonymous = Factory.CreateNoRedirectClient();
        var denied = await anonymous.GetAsync("/settings");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.StartsWith("/login", denied.Headers.Location!.OriginalString.Replace("http://localhost", ""));
    }

    [Fact]
    public async Task An_unknown_connected_agent_is_listed_as_unregistered()
    {
        await using var agent = await ConnectAsync("mystery-pc");
        using var client = await Factory.CreateAuthenticatedClientAsync();

        var body = await (await client.GetAsync("/settings")).Content.ReadAsStringAsync();

        Assert.Contains("Unregistered agents", body);
        Assert.Contains("mystery-pc", body);
        Assert.Contains("Create station", body);
        Assert.Equal("mystery-pc", Assert.Single(await Admin.GetUnregisteredAsync()).MachineId);
    }

    [Fact]
    public async Task Create_station_from_agent_registers_it_and_pushes_state_and_rejects_a_duplicate_name()
    {
        await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("mystery-pc");
        var typeId = await TypeAsync();

        var duplicate = await Admin.CreateStationFromAgentAsync("mystery-pc", "PC-01", typeId);
        Assert.False(duplicate.Success);
        Assert.Contains("already exists", duplicate.Error);
        Assert.Single(await Admin.GetUnregisteredAsync());

        var created = await Admin.CreateStationFromAgentAsync("mystery-pc", "PC-09", typeId);

        Assert.True(created.Success, created.Error);
        Assert.Equal(StationKind.PC, created.Value!.Kind);
        Assert.Equal("mystery-pc", created.Value.AgentMachineId);
        Assert.Empty(await Admin.GetUnregisteredAsync());
        await WaitUntilAsync(() => agent.Count("Lock") == 2, "state push after create");
        var lockInfo = (LockInfo)agent.Calls.Last().Args[0]!;
        Assert.Equal("PC-09", lockInfo.StationName);
        Assert.Equal(AgentCommandService.DefaultLockReason, lockInfo.Reason);
    }

    [Fact]
    public async Task Assign_agent_sets_the_machine_id_pushes_state_and_rejects_consoles_and_unknown_stations()
    {
        var pc = await PcStationAsync("PC-01", null);
        int console;
        using (var scope = Factory.Services.CreateScope())
        {
            console = (await scope.ServiceProvider.GetRequiredService<Cafe.Server.Stations.StationService>().CreateAsync(
                new Cafe.Server.Stations.StationInput { Name = "PS5", StationTypeId = await TypeAsync(), Kind = StationKind.Console })).Value!.Id;
        }

        await PrepaidAsync(pc, 60);
        await using var agent = await ConnectAsync("mystery-pc");
        Assert.Equal(1, agent.Count("Lock")); // unknown machine

        Assert.False((await Admin.AssignAgentToStationAsync("mystery-pc", console)).Success);
        var missing = await Admin.AssignAgentToStationAsync("mystery-pc", 9999);
        Assert.False(missing.Success);
        Assert.Equal(Cafe.Server.Stations.FailureKind.NotFound, missing.Kind);

        var assigned = await Admin.AssignAgentToStationAsync("mystery-pc", pc);

        Assert.True(assigned.Success, assigned.Error);
        await WaitUntilAsync(() => agent.Count("Unlock") == 1, "Unlock for the active session");
        Assert.Empty(await Admin.GetUnregisteredAsync());
    }

    [Fact]
    public async Task Cafe_name_is_trimmed_validated_and_used_on_the_next_lock()
    {
        var settings = Factory.Services.GetRequiredService<AgentSettingsService>();

        Assert.False((await settings.SaveCafeNameAsync("   ")).Success);
        Assert.False((await settings.SaveCafeNameAsync(new string('x', 61))).Success);
        Assert.True((await settings.SaveCafeNameAsync("  Level Up  ")).Success);
        Assert.Equal("Level Up", await settings.GetCafeNameAsync());

        await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("pc-01");
        Assert.Equal("Level Up", ((LockInfo)agent.Calls[0].Args[0]!).CafeName);
    }

    [Fact]
    public async Task Regenerating_the_key_changes_it_and_disconnects_agents()
    {
        var settings = Factory.Services.GetRequiredService<AgentSettingsService>();
        var before = await settings.GetOrCreateSharedKeyAsync();
        await using var agent = await ConnectAsync("pc-01");
        Assert.True(Factory.Services.GetRequiredService<AgentRegistry>().IsConnected("pc-01"));

        var after = await settings.RegenerateSharedKeyAsync();

        Assert.NotEqual(before, after);
        Assert.Equal(after, await settings.GetOrCreateSharedKeyAsync());
        await WaitUntilAsync(() => !Factory.Services.GetRequiredService<AgentRegistry>().IsConnected("pc-01"), "agent disconnected");
    }
}
