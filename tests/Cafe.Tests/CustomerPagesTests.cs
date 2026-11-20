using System.Net;
using Cafe.Server.Customers;
using Cafe.Server.Data;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>Prerendered smoke tests for /customers, /customers/{id} and /packages (CUST-01..CUST-04).</summary>
public class CustomerPagesTests : CustomerTestBase
{
    [Theory]
    [InlineData("/customers")]
    [InlineData("/customers/1")]
    [InlineData("/packages")]
    public async Task CUST01_anonymous_request_redirects_to_login_without_data(string path)
    {
        using var auth = await Factory.CreateAuthenticatedClientAsync();
        await CustomerAsync("Secret Sam", "0901234567");
        var anonymous = Factory.CreateNoRedirectClient();

        var response = await anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString.Replace("http://localhost", ""));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Secret Sam", body);
        Assert.DoesNotContain("0901234567", body);
    }

    [Fact]
    public async Task CUST01_customers_page_shows_empty_state_then_seeded_customer_and_nav_links()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();

        var empty = await client.GetStringAsync("/customers");
        Assert.Contains("No customers found", empty);
        Assert.Contains("Add customer", empty);
        Assert.Contains("href=\"customers\"", empty);
        Assert.Contains("href=\"packages\"", empty);

        await CustomerAsync("Alice Wong", "0901234567", member: true);
        var body = await client.GetStringAsync("/customers");

        Assert.Contains("Alice Wong", body);
        Assert.Contains("090 123 4567", body);
        Assert.Contains("Member", body);
        Assert.Contains("Showing up to 50", body);
        Assert.Contains("Show inactive", body);
    }

    [Fact]
    public async Task CUST01_customer_names_are_html_encoded()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        await CustomerAsync("<b>x</b>");

        var body = await client.GetStringAsync("/customers");

        Assert.DoesNotContain("<b>x</b>", body);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", body);
    }

    [Fact]
    public async Task CUST03_CUST04_detail_page_shows_balance_package_ledger_and_forms()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        var typeId = await TypeAsync("Standard");
        var station = await StationAsync("PC-01", typeId);
        var customer = await CustomerAsync("Alice Wong", "0901234567", member: true);
        var package = await PackageAsync("Evening Pack", 120, 150m);
        await SellAsync(customer, package, PaymentMethod.Card);
        await StartBalanceAsync(station, customer, 30);
        await PackageAsync("Spare Pack", 60, 80m);

        var body = await client.GetStringAsync($"/customers/{customer}");

        Assert.Contains("Alice Wong", body);
        Assert.Contains("Balance", body);
        Assert.Contains("Any type", body);
        Assert.Contains("Evening Pack", body);
        Assert.Contains("Package purchase", body);
        Assert.Contains("Session", body);
        Assert.Contains("Sell package", body);
        Assert.Contains("Receipts", body);
        Assert.Contains("Manual adjustment", body);
        Assert.Contains("Ledger history", body);
        Assert.Contains("Recent sessions", body);
        Assert.Contains("PC-01", body);
    }

    [Fact]
    public async Task CUST01_unknown_customer_shows_friendly_message_with_200()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/customers/99999");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(CustomerService.Gone, body);
        Assert.Contains("Back to customers", body);
    }

    [Fact]
    public async Task CUST04_packages_page_lists_active_and_inactive_packages()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        await PackageAsync("Night Owl", 180, 200m);
        var old = await PackageAsync("Retired Pack", 60, 90m);
        await InScopeAsync(sp => sp.GetRequiredService<PackageService>().SetActiveAsync(old, false));

        var body = await client.GetStringAsync("/packages");

        Assert.Contains("Night Owl", body);
        Assert.Contains("Retired Pack", body);
        Assert.Contains("Inactive", body);
        Assert.Contains("Add package", body);
    }
}
