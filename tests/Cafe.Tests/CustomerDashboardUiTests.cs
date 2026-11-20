using Cafe.Tests.Infrastructure;

namespace Cafe.Tests;

/// <summary>Dashboard card rendering for customers, members and balance sessions (CUST-05, CUST-06, PRIC-03).</summary>
public class CustomerDashboardUiTests : CustomerTestBase
{
    private static string Card(string html, string name)
    {
        var start = html.IndexOf($">{name}<", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} card not found");
        var next = html.IndexOf("station-card", start, StringComparison.Ordinal);
        return next < 0 ? html[start..] : html[start..next];
    }

    [Fact]
    public async Task CUST05_running_balance_session_shows_customer_member_tag_and_balance_caption()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        var station = await StationAsync("PC-01");
        var customer = await CustomerAsync("Alice Wong", "0901234567", member: true);
        await SellAsync(customer, await PackageAsync("Pack", 120, 150m));
        await StartBalanceAsync(station, customer, 30);

        var card = Card(await client.GetStringAsync("/"), "PC-01");

        Assert.Contains("Alice Wong", card);
        Assert.Contains("Member", card);
        Assert.Contains("Balance", card);
        Assert.Contains("Extend from balance", card);
    }

    [Fact]
    public async Task CUST06_member_priced_prepaid_session_shows_member_tag_but_guest_does_not()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        var memberStation = await StationAsync("PC-M");
        var guestStation = await StationAsync("PC-G");
        var member = await CustomerAsync("Mia Member", member: true);
        var guest = await CustomerAsync("Gus Guest");
        await SvcAsync(s => s.StartPrepaidAsync(memberStation, 60, member));
        await SvcAsync(s => s.StartPrepaidAsync(guestStation, 60, guest));

        var body = await client.GetStringAsync("/");

        Assert.Contains("Member", Card(body, "PC-M"));
        var guestCard = Card(body, "PC-G");
        Assert.Contains("Gus Guest", guestCard);
        Assert.DoesNotContain(">Member<", guestCard);
    }

    [Fact]
    public async Task CUST01_idle_card_offers_the_customer_search_control()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        await StationAsync("PC-01");

        var card = Card(await client.GetStringAsync("/"), "PC-01");

        Assert.Contains("aria-label=\"Customer search\"", card);
        Assert.Contains("Customer (optional)", card);
    }
}
