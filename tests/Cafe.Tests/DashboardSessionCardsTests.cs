using Cafe.Server.Data;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class DashboardSessionCardsTests : SessionTestBase
{
    private async Task<string> PageAsync()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        return await client.GetStringAsync("/");
    }

    /// <summary>The slice of the page from the station's name to the next card (or end).</summary>
    private static string Card(string html, string name)
    {
        var start = html.IndexOf($">{name}<", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} card not found");
        var next = html.IndexOf("station-card", start, StringComparison.Ordinal);
        return next < 0 ? html[start..] : html[start..next];
    }

    [Fact]
    public async Task Idle_card_offers_start_controls()
    {
        await StationAsync("PC-01");

        var card = Card(await PageAsync(), "PC-01");

        Assert.Contains("Idle", card);
        Assert.Contains("Start open-ended", card);
        Assert.Contains("Start prepaid", card);
        foreach (var m in new[] { "30", "60", "120", "180" })
        {
            Assert.Contains($">{m}</button>", card);
        }

        Assert.DoesNotContain("Pause", card);
        Assert.DoesNotContain("Extend", card);
    }

    [Fact]
    public async Task Prepaid_card_shows_countdown_and_all_controls()
    {
        var a = await StationAsync("PC-01");
        await StationAsync("PC-02");
        await PrepaidAsync(a, 60);

        var card = Card(await PageAsync(), "PC-01");

        Assert.Contains("Prepaid", card);
        Assert.Contains("1:00:00", card);
        Assert.Contains("In session", card);
        Assert.Contains("Pause", card);
        Assert.Contains("Extend", card);
        Assert.Contains("Move", card);
        Assert.Contains("Stop", card);
        Assert.Contains("PC-02", card);
        Assert.DoesNotContain("Start open-ended", card);
    }

    [Fact]
    public async Task Open_ended_card_shows_elapsed_and_no_extend()
    {
        var a = await StationAsync("PC-01");
        await OpenEndedAsync(a);
        Advance(TimeSpan.FromMinutes(5));

        var card = Card(await PageAsync(), "PC-01");

        Assert.Contains("Open-ended", card);
        Assert.Contains("05:00", card);
        Assert.Contains("est.", card);
        Assert.DoesNotContain("Extend", card);
        Assert.Contains("Pause", card);
    }

    [Fact]
    public async Task Grace_card_shows_time_up_and_grace_countdown()
    {
        var a = await StationAsync("PC-01");
        var session = await PrepaidAsync(a, 1);
        Advance(TimeSpan.FromMinutes(2));
        await UpdateAsync(session.Id, s =>
        {
            s.Status = SessionStatus.Grace;
            s.GraceStartedAtUtc = Factory.Clock.GetUtcNow();
        });

        var page = await PageAsync();
        var card = Card(page, "PC-01");

        Assert.Contains("TIME UP", card);
        Assert.Contains("Expiring", card);
        Assert.Contains("alert-timeup-level", page);
        Assert.Contains("Grace", card);
    }

    [Fact]
    public async Task Paused_card_shows_resume_and_paused_badge()
    {
        var a = await StationAsync("PC-01");
        var session = await PrepaidAsync(a, 60);
        Assert.True((await SvcAsync(s => s.PauseAsync(session.Id))).Success);

        var card = Card(await PageAsync(), "PC-01");

        Assert.Contains("Paused", card);
        Assert.Contains("Resume", card);
    }

    [Fact]
    public async Task Agent_station_without_session_shows_offline_and_start_controls()
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
            {
                Name = "PC-AG", StationTypeId = await TypeAsync(), Kind = StationKind.PC, AgentMachineId = "pc-ag",
            });
            Assert.True(created.Success, created.Error);
        }

        var card = Card(await PageAsync(), "PC-AG");

        Assert.Contains("Offline", card);
        Assert.Contains("Start open-ended", card);
    }

    [Fact]
    public async Task Session_status_overrides_offline()
    {
        int id;
        using (var scope = Factory.Services.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
            {
                Name = "PC-AG", StationTypeId = await TypeAsync(), Kind = StationKind.PC, AgentMachineId = "pc-ag",
            });
            id = created.Value!.Id;
        }

        await PrepaidAsync(id, 60);

        var card = Card(await PageAsync(), "PC-AG");

        Assert.Contains("In session", card);
        Assert.DoesNotContain("Offline", card);
    }
}
