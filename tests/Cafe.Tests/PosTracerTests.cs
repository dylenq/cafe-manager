using System.Net;
using Cafe.Server.Data;
using Cafe.Server.Payments;
using Cafe.Server.Products;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>
/// Phase 6 tracer (POS-01..POS-04, D-01/D-05/D-07/D-11): product -> order on a session -> Stop and settle ->
/// payment with allocations -> receipt page.
/// </summary>
public class PosTracerTests : PosTestBase
{
    private record Rung(int SessionId, int OrderId, StopQuote Quote);

    private async Task<Rung> RingAsync()
    {
        var stationId = await StationAsync("PC-01");
        var productId = await ProductAsync("Iced Coffee", 25000m);
        var session = await PrepaidAsync(stationId, 60);
        var order = await OrderAsync(session.Id, null, (productId, 2));
        var quote = (await Svc(sp => sp.GetRequiredService<SessionService>().QuoteStopAsync(session.Id))).Value!;
        return new Rung(session.Id, order.Id, quote);
    }

    private Task<ServiceResult<CheckoutResult>> CheckoutAsync(Rung r, decimal due, params TenderLine[] tenders) =>
        Svc(sp => sp.GetRequiredService<CheckoutService>()
            .StopAndSettleAsync(r.SessionId, r.Quote.Version, r.Quote.Amount, tenders, due));

    [Fact]
    public async Task POS03_stop_and_settle_writes_one_payment_with_session_and_order_allocations()
    {
        var r = await RingAsync();
        var order = await LoadOrderAsync(r.OrderId);
        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.Equal(50000m, order.Total);
        var line = Assert.Single(order.Lines);
        Assert.Equal("Iced Coffee", line.ProductNameSnapshot);
        Assert.Equal(25000m, line.UnitPriceSnapshot);
        Assert.Equal(50000m, line.LineTotal);
        Assert.Null(order.CustomerId);

        var result = await CheckoutAsync(r, 56000m, new TenderLine(PaymentMethod.Cash, 56000m));

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Value!.PaymentError);
        Assert.Equal(0m, result.Value.Settlement!.Change);
        Assert.Equal(SessionStatus.Ended, (await LoadAsync(r.SessionId)).Status);
        await using var db = await DbAsync();
        var payment = await db.Payments.Include(p => p.Allocations).SingleAsync();
        Assert.Equal(56000m, payment.Amount);
        Assert.Equal(PaymentMethod.Cash, payment.Method);
        Assert.Equal(2, payment.Allocations.Count);
        Assert.Equal(6000m, payment.Allocations.Single(a => a.TargetType == AllocationTargetType.Session).Amount);
        Assert.Equal(50000m, payment.Allocations.Single(a => a.TargetType == AllocationTargetType.Order).Amount);
        Assert.Equal(payment.Amount, payment.Allocations.Sum(a => a.Amount));
        Assert.Equal(OrderStatus.Paid, (await LoadOrderAsync(r.OrderId)).Status);
    }

    [Fact]
    public async Task POS04_receipt_page_renders_for_admin_and_redirects_anonymous()
    {
        var r = await RingAsync();
        var result = await CheckoutAsync(r, 56000m, new TenderLine(PaymentMethod.Cash, 56000m));
        var id = result.Value!.Settlement!.ReceiptPaymentId;
        using var admin = await Factory.CreateAuthenticatedClientAsync();

        var body = await admin.GetStringAsync($"/receipts/{id}");

        Assert.Contains(id.ToString("D6"), body);
        Assert.Contains("PC-01", body);
        Assert.Contains("Iced Coffee", body);
        Assert.Contains(56000m.ToString("C"), System.Net.WebUtility.HtmlDecode(body));

        var anonymous = Factory.CreateNoRedirectClient();
        var denied = await anonymous.GetAsync($"/receipts/{id}");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.StartsWith("/login", denied.Headers.Location!.OriginalString.Replace("http://localhost", ""));
    }

    [Fact]
    public async Task POS01_product_price_change_does_not_alter_order_line_snapshots()
    {
        var r = await RingAsync();
        await using (var db = await DbAsync())
        {
            var product = await db.Products.SingleAsync();
            product.Price = 99999m;
            await db.SaveChangesAsync();
        }

        var line = Assert.Single((await LoadOrderAsync(r.OrderId)).Lines);
        Assert.Equal(25000m, line.UnitPriceSnapshot);
        Assert.Equal(50000m, line.LineTotal);
    }

    [Fact]
    public async Task POS03_cash_overpay_returns_change_without_storing_it_and_over_applied_is_rejected()
    {
        var r = await RingAsync();

        var tooMuch = await CheckoutAsync(r, 56000m, new TenderLine(PaymentMethod.Cash, 60000m));

        Assert.True(tooMuch.Success, tooMuch.Error);
        Assert.Null(tooMuch.Value!.Settlement);
        Assert.Equal(SettlementService.OverpayMessage, tooMuch.Value.PaymentError);
        await using (var db = await DbAsync())
        {
            Assert.Empty(await db.Payments.ToListAsync());
            Assert.Empty(await db.PaymentAllocations.ToListAsync());
        }

        var settle = await Svc(sp => sp.GetRequiredService<SettlementService>().SettleAsync(new SettleRequest(
            [new SettleTarget(AllocationTargetType.Session, r.SessionId), new SettleTarget(AllocationTargetType.Order, r.OrderId)],
            [new TenderLine(PaymentMethod.Cash, 56000m, 100000m)], 56000m)));

        Assert.True(settle.Success, settle.Error);
        Assert.Equal(44000m, settle.Value!.Change);
        await using var db2 = await DbAsync();
        Assert.Equal(56000m, (await db2.Payments.SingleAsync()).Amount);
    }

    [Fact]
    public async Task POS03_underpayment_covers_session_first_and_leaves_the_order_open()
    {
        var r = await RingAsync();

        var result = await CheckoutAsync(r, 56000m, new TenderLine(PaymentMethod.Cash, 20000m));

        Assert.True(result.Success, result.Error);
        var settlement = result.Value!.Settlement!;
        Assert.Equal(36000m, settlement.Remaining);
        Assert.Equal(SessionStatus.Ended, (await LoadAsync(r.SessionId)).Status);
        await using var db = await DbAsync();
        var allocations = await db.PaymentAllocations.OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(6000m, allocations.Single(a => a.TargetType == AllocationTargetType.Session).Amount);
        Assert.Equal(14000m, allocations.Single(a => a.TargetType == AllocationTargetType.Order).Amount);
        Assert.Equal(OrderStatus.Open, (await LoadOrderAsync(r.OrderId)).Status);
    }
}
