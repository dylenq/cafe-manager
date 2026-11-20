using System.Net;
using Cafe.Server.Data;
using Cafe.Server.Payments;
using Cafe.Server.Receipts;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>Prerendered smoke tests for the Phase 6 customer receipts card, Settings receipts and the receipt page.</summary>
public class PosReceiptPagesTests : PosTestBase
{
    [Fact]
    public async Task Customer_page_with_a_paid_sale_shows_Receipts_and_Reprint()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        var customer = await CustomerAsync("Alice Wong");
        var package = await PackageAsync("Evening Pack", 120, 150m);
        await SellAsync(customer, package, PaymentMethod.Card);

        var body = await client.GetStringAsync($"/customers/{customer}");

        Assert.Contains("Receipts", body);
        Assert.Contains("Reprint", body);
        Assert.Contains("Evening Pack", body);
        Assert.DoesNotContain("No receipts yet", body);
    }

    [Fact]
    public async Task Customer_page_without_payments_shows_the_receipts_empty_state()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        var customer = await CustomerAsync("Bob");

        var body = await client.GetStringAsync($"/customers/{customer}");

        Assert.Contains("Receipts", body);
        Assert.Contains("No receipts yet", body);
        Assert.DoesNotContain("Reprint", body);
    }

    [Fact]
    public async Task Customer_page_sell_form_no_longer_offers_a_payment_method_select()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        var customer = await CustomerAsync("Cara");
        await PackageAsync("Hour", 60, 80m);

        var body = await client.GetStringAsync($"/customers/{customer}");

        Assert.Contains("Sell package", body);
        Assert.DoesNotContain("id=\"sell-method\"", body);
    }

    [Fact]
    public async Task Settings_page_shows_the_receipts_section_with_test_print()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();

        var body = await client.GetStringAsync("/settings");

        Assert.Contains("Receipts", body);
        Assert.Contains("Print test receipt", body);
        Assert.Contains("Windows printer", body);
        Assert.Contains("Cafe name", body);
        Assert.Contains("Footer", body);
    }

    [Fact]
    public async Task Receipt_page_with_print_parameter_renders_the_receipt()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();
        var customer = await CustomerAsync("Dana");
        var package = await PackageAsync("Hour", 60, 80m);
        var sale = await SellAsync(customer, package, PaymentMethod.Cash);
        int paymentId;
        using (var scope = Factory.Services.CreateScope())
        {
            var history = scope.ServiceProvider.GetRequiredService<PaymentHistoryService>();
            paymentId = (await history.ForCustomerAsync(customer)).First().RootPaymentId;
        }

        var response = await client.GetAsync($"/receipts/{paymentId}?print=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-testid=\"receipt\"", await response.Content.ReadAsStringAsync());
        Assert.NotNull(sale);
    }

    [Fact]
    public async Task Receipt_page_for_an_unknown_id_shows_the_friendly_message()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/receipts/99999");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(ReceiptService.NotFoundMessage, await response.Content.ReadAsStringAsync());
    }
}
