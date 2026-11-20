using Cafe.Server.Data;
using Cafe.Server.Orders;
using Cafe.Server.Payments;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>D-05 edges of the station-card stop flow, at service level: settle now, pay later, stale quote.</summary>
public class StopSettleFlowTests : PosTestBase
{
    private async Task<(int SessionId, int OrderA, int OrderB)> PrepaidWithTwoOrdersAsync()
    {
        var stationId = await StationAsync("PC-01");
        var coffee = await ProductAsync("Coffee", 25000m);
        var cake = await ProductAsync("Cake", 10000m);
        var session = await PrepaidAsync(stationId, 60);
        var a = await OrderAsync(session.Id, null, (coffee, 2));
        var b = await OrderAsync(session.Id, null, (cake, 1));
        return (session.Id, a.Id, b.Id);
    }

    private Task<StopSettleQuote> QuoteAsync(int sessionId) =>
        Svc(async sp => (await sp.GetRequiredService<CheckoutService>().QuoteStopAndSettleAsync(sessionId)).Value!);

    private Task<ServiceResult<CheckoutResult>> SettleAsync(int sessionId, StopSettleQuote q, decimal due, params TenderLine[] tenders) =>
        Svc(sp => sp.GetRequiredService<CheckoutService>()
            .StopAndSettleAsync(sessionId, q.Stop.Version, q.Stop.Amount, tenders, due));

    [Fact]
    public async Task Quote_total_due_is_the_charge_plus_both_open_orders()
    {
        var (sessionId, _, _) = await PrepaidWithTwoOrdersAsync();

        var quote = await QuoteAsync(sessionId);

        Assert.Equal(2, quote.OpenOrders.Count);
        Assert.Equal(60000m, quote.OpenOrders.Sum(o => o.Outstanding));
        Assert.Equal(quote.Stop.Amount + 60000m, quote.TotalDue);
    }

    [Fact]
    public async Task Stop_and_settle_in_full_cash_ends_the_session_pays_orders_and_writes_one_receipt_group()
    {
        var (sessionId, a, b) = await PrepaidWithTwoOrdersAsync();
        var quote = await QuoteAsync(sessionId);

        var result = await SettleAsync(sessionId, quote, quote.TotalDue, new TenderLine(PaymentMethod.Cash, quote.TotalDue));

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Value!.PaymentError);
        Assert.NotNull(result.Value.Settlement);
        Assert.Equal(SessionStatus.Ended, (await LoadAsync(sessionId)).Status);
        Assert.Equal(OrderStatus.Paid, (await LoadOrderAsync(a)).Status);
        Assert.Equal(OrderStatus.Paid, (await LoadOrderAsync(b)).Status);
        await using var db = await DbAsync();
        var payment = await db.Payments.SingleAsync();
        Assert.Equal(result.Value.Settlement!.ReceiptPaymentId, payment.Id);
        Assert.Equal(quote.TotalDue, payment.Amount);
    }

    [Fact]
    public async Task Stop_pay_later_ends_the_session_without_payment_and_open_tabs_list_charge_and_orders_then_settle_later_pays_all()
    {
        var (sessionId, a, b) = await PrepaidWithTwoOrdersAsync();
        var quote = await QuoteAsync(sessionId);

        var stopped = await Svc(sp => sp.GetRequiredService<SessionService>().StopAsync(sessionId, quote.Stop.Version, quote.Stop.Amount));

        Assert.True(stopped.Success, stopped.Error);
        Assert.Equal(SessionStatus.Ended, (await LoadAsync(sessionId)).Status);
        await using (var db = await DbAsync())
        {
            Assert.Equal(0, await db.Payments.CountAsync());
        }

        var tabs = await Svc(sp => sp.GetRequiredService<OpenTabsService>().GetOpenTabsAsync());
        var group = Assert.Single(tabs);
        Assert.Equal(sessionId, group.SessionId);
        Assert.Equal(3, group.Items.Count);
        Assert.Equal(quote.TotalDue, group.TotalOutstanding);

        var targets = group.Items.Select(i => new SettleTarget(i.Type, i.Id)).ToList();
        var settled = await Svc(sp => sp.GetRequiredService<SettlementService>()
            .SettleAsync(new SettleRequest(targets, [new TenderLine(PaymentMethod.Cash, group.TotalOutstanding)], group.TotalOutstanding)));

        Assert.True(settled.Success, settled.Error);
        Assert.Equal(OrderStatus.Paid, (await LoadOrderAsync(a)).Status);
        Assert.Equal(OrderStatus.Paid, (await LoadOrderAsync(b)).Status);
        Assert.Empty(await Svc(sp => sp.GetRequiredService<OpenTabsService>().GetOpenTabsAsync()));
    }

    [Fact]
    public async Task Stale_quote_after_an_extra_order_stops_nothing_pays_nothing_and_returns_the_changed_message()
    {
        var (sessionId, a, _) = await PrepaidWithTwoOrdersAsync();
        var quote = await QuoteAsync(sessionId);
        var extra = await ProductAsync("Water", 5000m);
        await OrderAsync(sessionId, null, (extra, 1));

        var result = await SettleAsync(sessionId, quote, quote.TotalDue, new TenderLine(PaymentMethod.Cash, quote.TotalDue));

        // WR-02: the full due (session + open orders) is checked under the settlement lock before the stop.
        Assert.False(result.Success);
        Assert.Equal(SettlementService.ChangedMessage, result.Error);
        Assert.NotEqual(SessionStatus.Ended, (await LoadAsync(sessionId)).Status);
        Assert.Equal(OrderStatus.Open, (await LoadOrderAsync(a)).Status);
        await using (var db = await DbAsync())
        {
            Assert.Equal(0, await db.Payments.CountAsync());
        }

        var fresh = await QuoteAsync(sessionId);
        Assert.Equal(quote.TotalDue + 5000m, fresh.TotalDue);
    }

    [Fact]
    public async Task A_fresh_quote_after_an_extra_order_includes_it_and_settles()
    {
        var (sessionId, _, _) = await PrepaidWithTwoOrdersAsync();
        var stale = await QuoteAsync(sessionId);
        var extra = await ProductAsync("Water", 5000m);
        await OrderAsync(sessionId, null, (extra, 1));

        var fresh = await QuoteAsync(sessionId);

        Assert.Equal(stale.TotalDue + 5000m, fresh.TotalDue);
        var result = await SettleAsync(sessionId, fresh, fresh.TotalDue, new TenderLine(PaymentMethod.Cash, fresh.TotalDue));
        Assert.True(result.Success, result.Error);
        Assert.Null(result.Value!.PaymentError);
    }

    [Fact]
    public async Task Balance_session_with_zero_charge_settles_only_the_order()
    {
        var stationId = await StationAsync("PC-02");
        var customerId = await CustomerAsync();
        var packageId = await PackageAsync("Hour", 60, 4000m);
        await SellAsync(customerId, packageId);
        var session = await StartBalanceAsync(stationId, customerId, 30);
        var coffee = await ProductAsync("Coffee", 25000m);
        var order = await OrderAsync(session.Id, customerId, (coffee, 1));

        var quote = await QuoteAsync(session.Id);
        Assert.Equal(0m, quote.Stop.Amount);
        Assert.Equal(25000m, quote.TotalDue);

        var result = await SettleAsync(session.Id, quote, quote.TotalDue, new TenderLine(PaymentMethod.Card, 25000m));

        Assert.True(result.Success, result.Error);
        Assert.Equal(OrderStatus.Paid, (await LoadOrderAsync(order.Id)).Status);
        await using var db = await DbAsync();
        var forSession = await db.PaymentAllocations.CountAsync(a => a.TargetType == AllocationTargetType.Session);
        Assert.Equal(0, forSession);
        Assert.Equal(order.Id, (await db.PaymentAllocations.SingleAsync(a => a.TargetType == AllocationTargetType.Order)).TargetId);
    }

    [Fact]
    public async Task Stop_with_nothing_owed_records_no_payment()
    {
        var stationId = await StationAsync("PC-03");
        var session = await OpenEndedAsync(stationId);
        var quote = await QuoteAsync(session.Id);
        Assert.Equal(0m, quote.TotalDue);

        var result = await SettleAsync(session.Id, quote, 0m);

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Value!.Settlement);
        Assert.Null(result.Value.PaymentError);
        Assert.Equal(SessionStatus.Ended, (await LoadAsync(session.Id)).Status);
        await using var db = await DbAsync();
        Assert.Equal(0, await db.Payments.CountAsync());
    }
}
