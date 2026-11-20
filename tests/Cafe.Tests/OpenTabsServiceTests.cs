using Cafe.Server.Data;
using Cafe.Server.Orders;
using Cafe.Server.Payments;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>POS-03 / D-05: the open tabs read model.</summary>
public class OpenTabsServiceTests : PosTestBase
{
    private Task<IReadOnlyList<OpenTabGroup>> Tabs() => Svc(sp => sp.GetRequiredService<OpenTabsService>().GetOpenTabsAsync());

    private Task<ServiceResult<SettleResult>> Pay(decimal amount, params SettleTarget[] targets) => PayDue(amount, amount, targets);

    private Task<ServiceResult<SettleResult>> PayDue(decimal amount, decimal due, params SettleTarget[] targets) =>
        Svc(sp => sp.GetRequiredService<SettlementService>().SettleAsync(
            new SettleRequest(targets, [new TenderLine(PaymentMethod.Cash, amount)], due)));

    private async Task SetCutoffAsync(DateTimeOffset value)
    {
        await using var db = await DbAsync();
        var setting = await db.Settings.SingleAsync(s => s.Key == "Pos:BillingStartUtc");
        setting.Value = value.UtcDateTime.ToString("O");
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task D05_groups_session_charge_and_its_orders_then_customer_orders_then_walk_ins()
    {
        var coke = await ProductAsync("Coke", 1000m);
        var customerId = await CustomerAsync("Alice");
        var session = await PrepaidAsync(await StationAsync("PC-01"), 60);
        var sessionOrder = await OrderAsync(session.Id, null, (coke, 1));
        await SvcAsync(s => s.StopAsync(session.Id));
        Advance(TimeSpan.FromMinutes(1));
        var customerOrder = await OrderAsync(null, customerId, (coke, 2));
        Advance(TimeSpan.FromMinutes(1));
        var walkIn = await OrderAsync(null, null, (coke, 3));

        var tabs = await Tabs();

        Assert.Equal(3, tabs.Count);
        Assert.Equal("PC-01", tabs[0].Header);
        Assert.Equal(session.Id, tabs[0].SessionId);
        Assert.Equal([AllocationTargetType.Session, AllocationTargetType.Order], tabs[0].Items.Select(i => i.Type));
        Assert.Equal(7000m, tabs[0].TotalOutstanding);
        Assert.Equal("Alice", tabs[1].Header);
        Assert.Equal(2000m, tabs[1].TotalOutstanding);
        Assert.Equal($"Walk-in order #{walkIn.Id}", tabs[2].Header);
        Assert.Equal(sessionOrder.Id, tabs[0].Items[1].Id);
        Assert.Equal(customerOrder.Id, tabs[1].Items[0].Id);
    }

    [Fact]
    public async Task D05_session_header_includes_the_customer_name()
    {
        var customerId = await CustomerAsync("Alice");
        await SellAsync(customerId, await PackageAsync("Hour", 60, 10m));
        var session = await StartBalanceAsync(await StationAsync("PC-01"), customerId, 30);
        var coke = await ProductAsync("Coke", 1000m);
        await OrderAsync(session.Id, null, (coke, 1));

        var group = Assert.Single(await Tabs());

        Assert.Equal("PC-01 (Alice) - open session", group.Header);
        Assert.Single(group.Items);
        Assert.Equal(AllocationTargetType.Order, group.Items[0].Type);
    }

    [Fact]
    public async Task D05_voided_paid_and_zero_outstanding_items_are_absent()
    {
        var coke = await ProductAsync("Coke", 1000m);
        var voided = await OrderAsync(null, null, (coke, 1));
        await Svc(sp => sp.GetRequiredService<OrderService>().VoidAsync(voided.Id));
        var paid = await OrderAsync(null, null, (coke, 1));
        await Pay(1000m, new SettleTarget(AllocationTargetType.Order, paid.Id));
        var free = await ProductAsync("Sample", 0m);
        await OrderAsync(null, null, (free, 1));

        Assert.Empty(await Tabs());
    }

    [Fact]
    public async Task D05_partial_payment_shows_only_the_remainder()
    {
        var coke = await ProductAsync("Coke", 1000m);
        var order = await OrderAsync(null, null, (coke, 5));
        await PayDue(2000m, 5000m, new SettleTarget(AllocationTargetType.Order, order.Id));

        var item = Assert.Single(Assert.Single(await Tabs()).Items);

        Assert.Equal((5000m, 2000m, 3000m), (item.Total, item.Paid, item.Outstanding));
    }

    [Fact]
    public async Task D05_a_session_that_ended_before_the_billing_cutoff_never_appears()
    {
        var session = await PrepaidAsync(await StationAsync("PC-01"), 60);
        await SvcAsync(s => s.StopAsync(session.Id));
        Assert.Single(await Tabs());

        await SetCutoffAsync(Factory.Clock.GetUtcNow().AddDays(1));

        Assert.Empty(await Tabs());
    }

    [Fact]
    public async Task D05_zero_charge_sessions_and_paid_sessions_are_absent()
    {
        var customerId = await CustomerAsync();
        await SellAsync(customerId, await PackageAsync("Hour", 60, 10m));
        var balance = await StartBalanceAsync(await StationAsync("PC-02"), customerId, 30);
        await SvcAsync(s => s.StopAsync(balance.Id));
        var paid = await PrepaidAsync(await StationAsync("PC-03"), 60);
        await SvcAsync(s => s.StopAsync(paid.Id));
        await Pay(6000m, new SettleTarget(AllocationTargetType.Session, paid.Id));

        Assert.Empty(await Tabs());
    }

    [Fact]
    public async Task D05_attachable_sessions_list_only_sessions_that_have_not_ended()
    {
        var running = await PrepaidAsync(await StationAsync("PC-01"), 60);
        var ended = await PrepaidAsync(await StationAsync("PC-02"), 60);
        await SvcAsync(s => s.StopAsync(ended.Id));

        var list = await Svc(sp => sp.GetRequiredService<OpenTabsService>().ListAttachableSessionsAsync());

        var one = Assert.Single(list);
        Assert.Equal(running.Id, one.SessionId);
        Assert.Equal("PC-01", one.StationName);
        Assert.Null(one.CustomerName);
    }
}
