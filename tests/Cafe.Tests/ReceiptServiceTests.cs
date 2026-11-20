using Cafe.Server.Data;
using Cafe.Server.Payments;
using Cafe.Server.Receipts;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class ReceiptServiceTests : PosTestBase
{
    private Task<ServiceResult<ReceiptDocument>> ReceiptAsync(int paymentId, decimal? change = null) =>
        Svc(sp => sp.GetRequiredService<ReceiptService>().GetAsync(paymentId, change));

    private async Task<(int SessionId, int Order1, int Order2, StopQuote Quote)> RingTabAsync()
    {
        var stationId = await StationAsync("PC-01");
        var coffee = await ProductAsync("Iced Coffee", 25000m);
        var tea = await ProductAsync("Tea", 10000m);
        var session = await PrepaidAsync(stationId, 60);
        var o1 = await OrderAsync(session.Id, null, (coffee, 2));
        var o2 = await OrderAsync(session.Id, null, (tea, 1));
        Advance(TimeSpan.FromMinutes(10));
        var quote = (await Svc(sp => sp.GetRequiredService<SessionService>().QuoteStopAsync(session.Id))).Value!;
        return (session.Id, o1.Id, o2.Id, quote);
    }

    private Task<ServiceResult<CheckoutResult>> CheckoutAsync(
        (int SessionId, int Order1, int Order2, StopQuote Quote) r, decimal due, params TenderLine[] tenders) =>
        Svc(sp => sp.GetRequiredService<CheckoutService>()
            .StopAndSettleAsync(r.SessionId, r.Quote.Version, r.Quote.Amount, tenders, due));

    [Fact]
    public async Task Session_and_two_tab_orders_settled_together_make_one_document()
    {
        var r = await RingTabAsync();
        var due = r.Quote.Amount + 60000m;
        var result = await CheckoutAsync(r, due, new TenderLine(PaymentMethod.Cash, due));
        Assert.True(result.Success, result.Error);

        var doc = (await ReceiptAsync(result.Value!.Settlement!.ReceiptPaymentId)).Value!;

        Assert.Equal(3, doc.Targets.Count);
        Assert.Equal(1, doc.Targets.Count(t => t.Kind == ReceiptTargetKind.Session));
        Assert.Equal(2, doc.Targets.Count(t => t.Kind == ReceiptTargetKind.Order));
        Assert.Equal(due, doc.Subtotal);
        Assert.Equal(due, doc.Paid);
        Assert.Equal(0m, doc.Remaining);
        var session = doc.Targets.Single(t => t.Kind == ReceiptTargetKind.Session);
        Assert.Equal("PC-01", session.Title);
        Assert.Equal("10 min, Prepaid", session.Detail);
    }

    [Fact]
    public async Task Split_checkout_resolves_to_the_same_root_and_number_from_either_payment_id()
    {
        var r = await RingTabAsync();
        var due = r.Quote.Amount + 60000m;
        var result = await CheckoutAsync(r, due,
            new TenderLine(PaymentMethod.Cash, 40000m), new TenderLine(PaymentMethod.Card, due - 40000m));
        Assert.True(result.Success, result.Error);
        var ids = result.Value!.Settlement!.PaymentIds;
        Assert.Equal(2, ids.Count);

        var first = (await ReceiptAsync(ids[0])).Value!;
        var second = (await ReceiptAsync(ids[1])).Value!;

        Assert.Equal(first.Number, second.Number);
        Assert.Equal(ids[0].ToString("D6"), first.Number);
        Assert.Equal([PaymentMethod.Cash, PaymentMethod.Card], first.Payments.Select(p => p.Method));
        Assert.Equal(due, first.Paid);
        Assert.Equal(first.Subtotal, second.Subtotal);
    }

    [Fact]
    public async Task Reprint_after_a_later_settle_still_shows_the_remaining_as_of_that_receipt_and_change_only_when_passed()
    {
        var r = await RingTabAsync();
        var due = r.Quote.Amount + 60000m;
        var partial = await CheckoutAsync(r, due, new TenderLine(PaymentMethod.Cash, r.Quote.Amount + 20000m));
        Assert.True(partial.Success, partial.Error);
        var id = partial.Value!.Settlement!.ReceiptPaymentId;

        var before = (await ReceiptAsync(id)).Value!;
        Assert.Equal(30000m, before.Remaining);

        var settle = await Svc(sp => sp.GetRequiredService<SettlementService>().SettleAsync(new SettleRequest(
            [new SettleTarget(AllocationTargetType.Order, r.Order1)],
            [new TenderLine(PaymentMethod.Cash, 30000m)], 30000m)));
        Assert.True(settle.Success, settle.Error);

        // WR-04: the reprint reconciles with the original receipt (Remaining 30000), not with today's balance.
        var after = (await ReceiptAsync(id, change: 1500m)).Value!;
        Assert.Equal(30000m, after.Remaining);
        Assert.Equal(before.Subtotal - before.Paid, after.Remaining);
        Assert.Equal(1500m, after.Change);

        // The later settlement's own receipt shows the debt cleared.
        var later = (await ReceiptAsync(settle.Value!.ReceiptPaymentId)).Value!;
        Assert.Equal(0m, later.Remaining);
        Assert.Null((await ReceiptAsync(id)).Value!.Change);
    }

    [Fact]
    public async Task Package_sale_receipt_shows_name_and_minutes()
    {
        var customerId = await CustomerAsync("Alice");
        var packageId = await PackageAsync("Night Owl", 180, 90000m);
        int saleId;
        await using (var db = await DbAsync())
        {
            var sale = new PackageSale
            {
                CustomerId = customerId, TimePackageId = packageId, Price = 90000m, Minutes = 180, AtUtc = Start,
            };
            db.PackageSales.Add(sale);
            await db.SaveChangesAsync();
            saleId = sale.Id;
        }

        var settle = await Svc(sp => sp.GetRequiredService<SettlementService>().SettleAsync(new SettleRequest(
            [new SettleTarget(AllocationTargetType.PackageSale, saleId)], [new TenderLine(PaymentMethod.Card, 90000m)], 90000m)));
        Assert.True(settle.Success, settle.Error);

        var doc = (await ReceiptAsync(settle.Value!.ReceiptPaymentId)).Value!;

        var target = Assert.Single(doc.Targets);
        Assert.Equal(ReceiptTargetKind.Package, target.Kind);
        Assert.Equal("Night Owl", target.Title);
        Assert.Equal("180 min", target.Detail);
        Assert.Equal(90000m, doc.Subtotal);
    }

    [Fact]
    public async Task Unknown_payment_id_is_not_found()
    {
        var result = await ReceiptAsync(999);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.NotFound, result.Kind);
    }

    [Fact]
    public async Task Header_uses_defaults_then_the_receipt_settings()
    {
        var stationId = await StationAsync("PC-01");
        var coffee = await ProductAsync("Iced Coffee", 25000m);
        var session = await PrepaidAsync(stationId, 60);
        await OrderAsync(session.Id, null, (coffee, 1));
        var quote = (await Svc(sp => sp.GetRequiredService<SessionService>().QuoteStopAsync(session.Id))).Value!;
        var due = quote.Amount + 25000m;
        var done = await Svc(sp => sp.GetRequiredService<CheckoutService>()
            .StopAndSettleAsync(session.Id, quote.Version, quote.Amount, [new TenderLine(PaymentMethod.Cash, due)], due));
        var id = done.Value!.Settlement!.ReceiptPaymentId;

        var plain = (await ReceiptAsync(id)).Value!;
        Assert.Equal("Cafe", plain.Header.CafeName);
        Assert.Empty(plain.Header.Lines);
        Assert.Equal("Thank you!", plain.Header.Footer);

        await using (var db = await DbAsync())
        {
            db.Settings.AddRange(
                new Setting { Key = "Receipts:CafeName", Value = "Pixel Den" },
                new Setting { Key = "Receipts:Address", Value = "1 Main St" },
                new Setting { Key = "Receipts:Phone", Value = "555-0100" },
                new Setting { Key = "Receipts:Footer", Value = "See you soon" });
            await db.SaveChangesAsync();
        }

        var custom = (await ReceiptAsync(id)).Value!;
        Assert.Equal("Pixel Den", custom.Header.CafeName);
        Assert.Equal(["1 Main St", "555-0100"], custom.Header.Lines);
        Assert.Equal("See you soon", custom.Header.Footer);
    }
}
