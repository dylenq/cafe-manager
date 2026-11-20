using Cafe.Server.Data;
using Cafe.Server.Orders;
using Cafe.Server.Payments;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>POS-03 / D-04, D-05: settlement edge cases, the stop-and-settle quote and payment history.</summary>
public class SettlementServiceTests : PosTestBase
{
    private Task<ServiceResult<SettleResult>> Settle(SettleRequest request) =>
        Svc(sp => sp.GetRequiredService<SettlementService>().SettleAsync(request));

    private static SettleTarget Order(int id) => new(AllocationTargetType.Order, id);

    private static SettleRequest Request(decimal due, IEnumerable<SettleTarget> targets, params TenderLine[] tenders) =>
        new(targets.ToList(), tenders, due);

    private async Task<(int O1, int O2)> TwoOrdersAsync(int q1 = 6, int q2 = 2)
    {
        var id = await ProductAsync("Item", 1000m);
        return ((await OrderAsync(null, null, (id, q1))).Id, (await OrderAsync(null, null, (id, q2))).Id);
    }

    private async Task<int> PaymentCountAsync()
    {
        await using var db = await DbAsync();
        return await db.Payments.CountAsync();
    }

    [Fact]
    public async Task D04_split_cash_and_card_writes_two_payments_sharing_the_primary_with_waterfall_allocations()
    {
        var (o1, o2) = await TwoOrdersAsync();

        var result = await Settle(Request(8000m, [Order(o1), Order(o2)],
            new TenderLine(PaymentMethod.Cash, 5000m), new TenderLine(PaymentMethod.Card, 3000m)));

        Assert.True(result.Success, result.Error);
        await using var db = await DbAsync();
        var payments = await db.Payments.Include(p => p.Allocations).OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(2, payments.Count);
        Assert.Equal(PaymentMethod.Cash, payments[0].Method);
        Assert.Equal(PaymentMethod.Card, payments[1].Method);
        Assert.Null(payments[0].PrimaryPaymentId);
        Assert.Equal(payments[0].Id, payments[1].PrimaryPaymentId);
        Assert.Equal(payments[0].Id, result.Value!.ReceiptPaymentId);
        Assert.Equal(8000m, payments.SelectMany(p => p.Allocations).Sum(a => a.Amount));
        Assert.Equal(5000m, payments[0].Allocations.Single(a => a.TargetId == o1).Amount);
        Assert.Equal(1000m, payments[1].Allocations.Single(a => a.TargetId == o1).Amount);
        Assert.Equal(2000m, payments[1].Allocations.Single(a => a.TargetId == o2).Amount);
        Assert.Equal(OrderStatus.Paid, (await LoadOrderAsync(o1)).Status);
        Assert.Equal(OrderStatus.Paid, (await LoadOrderAsync(o2)).Status);
    }

    [Fact]
    public async Task D04_cash_tendered_above_the_amount_returns_change_and_stores_only_the_applied_amount()
    {
        var (o1, _) = await TwoOrdersAsync(8, 1);

        var result = await Settle(Request(8000m, [Order(o1)], new TenderLine(PaymentMethod.Cash, 8000m, 10000m)));

        Assert.True(result.Success, result.Error);
        Assert.Equal(2000m, result.Value!.Change);
        await using var db = await DbAsync();
        Assert.Equal(8000m, (await db.Payments.SingleAsync()).Amount);
    }

    [Fact]
    public async Task D04_card_with_tendered_and_tendered_below_amount_are_rejected()
    {
        var (o1, _) = await TwoOrdersAsync(8, 1);

        Assert.False((await Settle(Request(8000m, [Order(o1)], new TenderLine(PaymentMethod.Card, 8000m, 8000m)))).Success);
        Assert.False((await Settle(Request(8000m, [Order(o1)], new TenderLine(PaymentMethod.Cash, 8000m, 7000m)))).Success);
        Assert.False((await Settle(Request(8000m, [Order(o1)], new TenderLine(PaymentMethod.Cash, 0m)))).Success);
        Assert.False((await Settle(Request(8000m, [Order(o1)], new TenderLine(PaymentMethod.Cash, -5m)))).Success);
        Assert.Equal(0, await PaymentCountAsync());
    }

    [Fact]
    public async Task D04_applied_above_the_due_is_rejected_and_writes_nothing()
    {
        var (o1, _) = await TwoOrdersAsync(8, 1);

        var result = await Settle(Request(8000m, [Order(o1)],
            new TenderLine(PaymentMethod.Cash, 5000m), new TenderLine(PaymentMethod.Card, 4000m)));

        Assert.False(result.Success);
        Assert.Equal(SettlementService.OverDue, result.Error);
        Assert.Equal(0, await PaymentCountAsync());
    }

    [Fact]
    public async Task D04_partial_payment_leaves_the_order_open_and_the_remainder_can_be_paid_later()
    {
        var (o1, o2) = await TwoOrdersAsync();

        var first = await Settle(Request(8000m, [Order(o1), Order(o2)], new TenderLine(PaymentMethod.Cash, 3000m)));

        Assert.True(first.Success, first.Error);
        Assert.Equal(5000m, first.Value!.Remaining);
        Assert.Equal(OrderStatus.Open, (await LoadOrderAsync(o1)).Status);
        Assert.Equal(OrderStatus.Open, (await LoadOrderAsync(o2)).Status);

        var second = await Settle(Request(5000m, [Order(o1), Order(o2)], new TenderLine(PaymentMethod.Cash, 5000m)));

        Assert.True(second.Success, second.Error);
        Assert.Equal(OrderStatus.Paid, (await LoadOrderAsync(o1)).Status);
        Assert.Equal(OrderStatus.Paid, (await LoadOrderAsync(o2)).Status);
    }

    [Fact]
    public async Task D05_stale_expected_due_returns_the_changed_message_and_writes_nothing()
    {
        var (o1, _) = await TwoOrdersAsync(8, 1);

        var result = await Settle(Request(7000m, [Order(o1)], new TenderLine(PaymentMethod.Cash, 7000m)));

        Assert.False(result.Success);
        Assert.Equal(SettlementService.ChangedMessage, result.Error);
        Assert.Equal(0, await PaymentCountAsync());
    }

    [Fact]
    public async Task D05_settling_a_voided_order_or_a_running_session_is_refused()
    {
        var (o1, _) = await TwoOrdersAsync(8, 1);
        await Svc(sp => sp.GetRequiredService<OrderService>().VoidAsync(o1));
        var session = await PrepaidAsync(await StationAsync(), 60);

        var voided = await Settle(Request(8000m, [Order(o1)], new TenderLine(PaymentMethod.Cash, 8000m)));
        var running = await Settle(Request(6000m, [new SettleTarget(AllocationTargetType.Session, session.Id)], new TenderLine(PaymentMethod.Cash, 6000m)));

        Assert.False(voided.Success);
        Assert.False(running.Success);
        Assert.Equal(SettlementService.StopFirst, running.Error);
        Assert.Equal(0, await PaymentCountAsync());
    }

    [Fact]
    public async Task D05_two_concurrent_settlements_of_one_order_produce_exactly_one_payment()
    {
        var (o1, _) = await TwoOrdersAsync(8, 1);
        var request = Request(8000m, [Order(o1)], new TenderLine(PaymentMethod.Cash, 8000m));

        var results = await Task.WhenAll(Settle(request), Settle(request));

        Assert.Single(results, r => r.Success);
        var loser = Assert.Single(results, r => !r.Success);
        Assert.Equal(SettlementService.ChangedMessage, loser.Error);
        Assert.Equal(1, await PaymentCountAsync());
    }

    [Fact]
    public async Task D05_session_two_orders_and_a_package_sale_in_one_payment_allocate_each_target_once()
    {
        var (o1, o2) = await TwoOrdersAsync(2, 3);
        var session = await PrepaidAsync(await StationAsync(), 60);
        await SvcAsync(s => s.StopAsync(session.Id));
        var customerId = await CustomerAsync();
        var sale = await SaleOwingAsync(customerId, await PackageAsync("Hour", 60, 4000m));
        SettleTarget[] targets =
        [
            new(AllocationTargetType.Session, session.Id), Order(o1), Order(o2), new(AllocationTargetType.PackageSale, sale),
        ];

        var result = await Settle(Request(6000m + 2000m + 3000m + 4000m, targets, new TenderLine(PaymentMethod.Cash, 15000m)));

        Assert.True(result.Success, result.Error);
        await using var db = await DbAsync();
        var allocations = await db.PaymentAllocations.ToListAsync();
        Assert.Equal(4, allocations.Count);
        Assert.Equal(4, allocations.Select(a => (a.TargetType, a.TargetId)).Distinct().Count());
        Assert.Equal(15000m, allocations.Sum(a => a.Amount));
        var payment = await db.Payments.SingleAsync();
        Assert.Equal(session.Id, payment.SessionId);
    }

    [Fact]
    public async Task D05_failed_save_leaves_no_payment_or_allocation_and_orders_stay_open()
    {
        var (o1, _) = await TwoOrdersAsync(8, 1);
        var failing = new SettlementService(new FailingSaveDbContextFactory(Factory.ConnectionString), Factory.Clock, new SettlementLock());

        var result = await failing.SettleAsync(Request(8000m, [Order(o1)], new TenderLine(PaymentMethod.Cash, 8000m)));

        Assert.False(result.Success);
        await using var db = await DbAsync();
        Assert.Equal(0, await db.Payments.CountAsync());
        Assert.Equal(0, await db.PaymentAllocations.CountAsync());
        Assert.Equal(OrderStatus.Open, (await LoadOrderAsync(o1)).Status);
    }

    [Fact]
    public async Task D05_preview_shows_total_paid_and_outstanding_for_every_target_including_paid_ones()
    {
        var (o1, o2) = await TwoOrdersAsync();
        await Settle(Request(6000m, [Order(o1)], new TenderLine(PaymentMethod.Cash, 6000m)));

        var preview = await Svc(sp => sp.GetRequiredService<SettlementService>().PreviewAsync([Order(o1), Order(o2)]));

        Assert.True(preview.Success, preview.Error);
        var items = preview.Value!;
        Assert.Equal(2, items.Count);
        Assert.Equal((6000m, 6000m, 0m), (items[0].Total, items[0].Paid, items[0].Outstanding));
        Assert.Equal((2000m, 0m, 2000m), (items[1].Total, items[1].Paid, items[1].Outstanding));
    }

    // ---- stop and settle ----

    [Fact]
    public async Task D05_quote_adds_the_sessions_open_orders_to_the_stop_charge()
    {
        var session = await PrepaidAsync(await StationAsync(), 60);
        var p = await ProductAsync("Coke", 1000m);
        await OrderAsync(session.Id, null, (p, 2));
        var paidOrder = await OrderAsync(session.Id, null, (p, 1));
        await Settle(Request(1000m, [Order(paidOrder.Id)], new TenderLine(PaymentMethod.Cash, 400m)));

        var quote = await Svc(sp => sp.GetRequiredService<CheckoutService>().QuoteStopAndSettleAsync(session.Id));

        Assert.True(quote.Success, quote.Error);
        Assert.Equal(6000m, quote.Value!.Stop.Amount);
        Assert.Equal(2, quote.Value.OpenOrders.Count);
        Assert.Equal(6000m + 2000m + 600m, quote.Value.TotalDue);
    }

    [Fact]
    public async Task D05_quote_for_an_ended_or_missing_session_fails_like_the_stop_quote()
    {
        var session = await PrepaidAsync(await StationAsync(), 60);
        await SvcAsync(s => s.StopAsync(session.Id));

        var ended = await Svc(sp => sp.GetRequiredService<CheckoutService>().QuoteStopAndSettleAsync(session.Id));
        var missing = await Svc(sp => sp.GetRequiredService<CheckoutService>().QuoteStopAndSettleAsync(9999));

        Assert.False(ended.Success);
        Assert.Equal(FailureKind.NotFound, missing.Kind);
    }

    [Fact]
    public async Task D05_stop_and_settle_with_zero_charge_and_no_tabs_stops_without_a_payment()
    {
        var (session, quote) = await BalanceSessionAsync();
        var before = await PaymentCountAsync();

        var result = await Svc(sp => sp.GetRequiredService<CheckoutService>()
            .StopAndSettleAsync(session.Id, quote.Stop.Version, quote.Stop.Amount, [], 0m));

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Value!.Settlement);
        Assert.Null(result.Value.PaymentError);
        Assert.Equal(SessionStatus.Ended, (await LoadAsync(session.Id)).Status);
        Assert.Equal(before, await PaymentCountAsync());
    }

    [Fact]
    public async Task D05_stop_and_settle_with_zero_charge_and_a_tab_order_settles_only_the_order()
    {
        var (session, _) = await BalanceSessionAsync();
        var order = await OrderAsync(session.Id, null, (await ProductAsync("Coke", 1000m), 2));
        var quote = (await Svc(sp => sp.GetRequiredService<CheckoutService>().QuoteStopAndSettleAsync(session.Id))).Value!;
        Assert.Equal(2000m, quote.TotalDue);

        var result = await Svc(sp => sp.GetRequiredService<CheckoutService>().StopAndSettleAsync(
            session.Id, quote.Stop.Version, quote.Stop.Amount, [new TenderLine(PaymentMethod.Cash, 2000m)], quote.TotalDue));

        Assert.True(result.Success, result.Error);
        await using var db = await DbAsync();
        var allocation = await db.PaymentAllocations.SingleAsync();
        Assert.Equal(AllocationTargetType.Order, allocation.TargetType);
        Assert.Equal(order.Id, allocation.TargetId);
    }

    [Fact]
    public async Task D05_stop_with_a_stale_version_settles_nothing()
    {
        var session = await PrepaidAsync(await StationAsync(), 60);
        var quote = (await Svc(sp => sp.GetRequiredService<CheckoutService>().QuoteStopAndSettleAsync(session.Id))).Value!;
        await SvcAsync(s => s.PauseAsync(session.Id));

        var result = await Svc(sp => sp.GetRequiredService<CheckoutService>().StopAndSettleAsync(
            session.Id, quote.Stop.Version, quote.Stop.Amount, [new TenderLine(PaymentMethod.Cash, 6000m)], 6000m));

        Assert.False(result.Success);
        Assert.Equal(SessionService.ChangedMessage, result.Error);
        Assert.NotEqual(SessionStatus.Ended, (await LoadAsync(session.Id)).Status);
        Assert.Equal(0, await PaymentCountAsync());
    }

    [Fact]
    public async Task D05_tenders_short_by_100_stop_the_session_and_leave_the_remainder_in_open_tabs()
    {
        var session = await PrepaidAsync(await StationAsync(), 60);
        var quote = (await Svc(sp => sp.GetRequiredService<CheckoutService>().QuoteStopAndSettleAsync(session.Id))).Value!;

        var result = await Svc(sp => sp.GetRequiredService<CheckoutService>().StopAndSettleAsync(
            session.Id, quote.Stop.Version, quote.Stop.Amount, [new TenderLine(PaymentMethod.Cash, 5900m)], quote.TotalDue));

        Assert.True(result.Success, result.Error);
        Assert.Equal(100m, result.Value!.Settlement!.Remaining);
        Assert.Equal(SessionStatus.Ended, (await LoadAsync(session.Id)).Status);
        var tabs = await Svc(sp => sp.GetRequiredService<OpenTabsService>().GetOpenTabsAsync());
        Assert.Equal(100m, Assert.Single(tabs).TotalOutstanding);
    }

    [Fact]
    public async Task D05_payment_failure_after_a_successful_stop_still_returns_success_with_the_error()
    {
        var session = await PrepaidAsync(await StationAsync(), 60);
        var quote = (await Svc(sp => sp.GetRequiredService<CheckoutService>().QuoteStopAndSettleAsync(session.Id))).Value!;

        var result = await Svc(sp => sp.GetRequiredService<CheckoutService>().StopAndSettleAsync(
            session.Id, quote.Stop.Version, quote.Stop.Amount, [new TenderLine(PaymentMethod.Cash, 6000m)], 5000m));

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Value!.PaymentError);
        Assert.Null(result.Value.Settlement);
        Assert.Equal(SessionStatus.Ended, (await LoadAsync(session.Id)).Status);
    }

    // ---- payment history ----

    [Fact]
    public async Task D08_recent_receipts_group_split_payments_under_one_root_newest_first()
    {
        var (o1, o2) = await TwoOrdersAsync();
        await Settle(Request(6000m, [Order(o1)], new TenderLine(PaymentMethod.Cash, 6000m)));
        Advance(TimeSpan.FromMinutes(1));
        var split = await Settle(Request(2000m, [Order(o2)],
            new TenderLine(PaymentMethod.Cash, 1500m), new TenderLine(PaymentMethod.Card, 500m)));

        var recent = await Svc(sp => sp.GetRequiredService<PaymentHistoryService>().RecentAsync());

        Assert.Equal(2, recent.Count);
        Assert.Equal(split.Value!.ReceiptPaymentId, recent[0].RootPaymentId);
        Assert.Equal(2000m, recent[0].Total);
        Assert.Equal($"Cash {1500m:#,##0.##} + Card {500m:#,##0.##}", recent[0].Methods);
        Assert.Contains($"#{o2}", recent[0].Label);
        Assert.DoesNotContain("$", recent[0].Methods);
    }

    [Fact]
    public async Task D08_customer_receipts_include_only_that_customers_orders()
    {
        var alice = await CustomerAsync("Alice");
        var bob = await CustomerAsync("Bob");
        var p = await ProductAsync("Coke", 1000m);
        var aliceOrder = await OrderAsync(null, alice, (p, 1));
        var bobOrder = await OrderAsync(null, bob, (p, 1));
        await Settle(Request(1000m, [Order(aliceOrder.Id)], new TenderLine(PaymentMethod.Cash, 1000m)));
        await Settle(Request(1000m, [Order(bobOrder.Id)], new TenderLine(PaymentMethod.Cash, 1000m)));

        var receipts = await Svc(sp => sp.GetRequiredService<PaymentHistoryService>().ForCustomerAsync(alice));

        Assert.Single(receipts);
        Assert.DoesNotContain(receipts, r => r.Label.Contains($"#{bobOrder.Id}"));
    }

    private async Task<(Session Session, StopSettleQuote Quote)> BalanceSessionAsync()
    {
        var customerId = await CustomerAsync();
        await SellAsync(customerId, await PackageAsync("Hour", 60, 10m));
        var session = await StartBalanceAsync(await StationAsync("PC-02"), customerId, 30);
        var quote = (await Svc(sp => sp.GetRequiredService<CheckoutService>().QuoteStopAndSettleAsync(session.Id))).Value!;
        Assert.Equal(0m, quote.Stop.Amount);
        return (session, quote);
    }

    /// <summary>A package sale with no allocation: what a Phase 5 sale looked like before the payment path.</summary>
    private async Task<int> SaleOwingAsync(int customerId, int packageId)
    {
        await using var db = await DbAsync();
        var package = await db.TimePackages.SingleAsync(p => p.Id == packageId);
        var sale = new PackageSale
        {
            CustomerId = customerId, TimePackageId = packageId, Price = package.Price, Minutes = package.Minutes, AtUtc = Factory.Clock.GetUtcNow(),
        };
        db.PackageSales.Add(sale);
        await db.SaveChangesAsync();
        return sale.Id;
    }
}
