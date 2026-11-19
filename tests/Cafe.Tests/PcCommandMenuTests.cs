using System.Text.RegularExpressions;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class PcCommandMenuTests : AgentTestBase
{
    private async Task<int> ConsoleStationAsync(string name)
    {
        using var scope = Factory.Services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
        {
            Name = name, StationTypeId = await TypeAsync(), Kind = StationKind.Console,
        });
        Assert.True(created.Success, created.Error);
        return created.Value!.Id;
    }

    private static int MenuCount(string html) => Regex.Matches(html, "class=\"pc-command-menu ").Count;

    [Fact]
    public async Task Menu_appears_only_on_pc_cards_that_have_an_agent()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        await PcStationAsync("PC-01", "pc-01");
        await PcStationAsync("PC-NOAGENT", machineId: null);
        await ConsoleStationAsync("PS5-1");

        var body = await client.GetStringAsync("/");

        Assert.Equal(1, MenuCount(body));
        Assert.Contains("PC-01", body);
        Assert.Contains("PS5-1", body);
        Assert.Contains("PC-NOAGENT", body);
    }

    [Fact]
    public async Task Running_session_on_a_disconnected_pc_shows_the_agent_offline_badge_but_a_connected_one_does_not()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await PrepaidAsync(stationId, 60);

        var offline = await client.GetStringAsync("/");
        Assert.Contains("Agent offline", offline);

        await using var agent = new RecordingAgentClient(Factory, await KeyAsync(), "pc-01");
        await agent.StartAsync();
        await WaitUntilAsync(() => agent.Calls.Count >= 1, "agent connected");

        var online = await client.GetStringAsync("/");
        Assert.DoesNotContain("Agent offline", online);
        Assert.DoesNotContain("PC agent offline", online);
    }

    [Fact]
    public async Task Idle_disconnected_pc_has_no_session_badge()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        await PcStationAsync("PC-01", "pc-01");

        var body = await client.GetStringAsync("/");

        Assert.DoesNotContain("Agent offline", body); // the badge is for running sessions only
    }

    [Fact]
    public async Task Menu_trigger_is_disabled_with_a_title_while_the_agent_is_offline()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        await PcStationAsync("PC-01", "pc-01");

        var body = await client.GetStringAsync("/");
        var trigger = Regex.Match(body, "<button[^>]*pc-menu-trigger[^>]*>").Value;

        Assert.Contains("disabled", trigger);
        Assert.Contains("title=\"PC agent offline\"", trigger);

        await using var agent = new RecordingAgentClient(Factory, await KeyAsync(), "pc-01");
        await agent.StartAsync();
        await WaitUntilAsync(() => agent.Calls.Count >= 1, "agent connected");

        var online = Regex.Match(await client.GetStringAsync("/"), "<button[^>]*pc-menu-trigger[^>]*>").Value;
        Assert.DoesNotContain("disabled", online);
        Assert.Contains("title=\"PC commands\"", online);
    }

    [Fact]
    public async Task Starting_a_session_on_an_offline_pc_succeeds()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");

        var prepaid = await PrepaidAsync(stationId, 30);

        Assert.Equal(Cafe.Server.Data.SessionStatus.Active, (await LoadAsync(prepaid.Id)).Status);
    }
}
