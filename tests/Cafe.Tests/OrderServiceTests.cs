using Cafe.Server.Data;
using Cafe.Server.Orders;
using Cafe.Server.Payments;
using Cafe.Server.Products;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>POS-02 / D-02, D-03: order creation rules, void rules and queries.</summary>
public class OrderServiceTests : PosTestBase
{
    private Task<ServiceResult<Order>> Create(OrderDraft draft) =>
        Svc(sp => sp.GetRequiredService<OrderService>().CreateAsync(draft));

    private Task<ServiceResult> Void(int id) => Svc(sp => sp.GetRequiredService<OrderService>().VoidAsync(id));

    private static OrderDraft Draft(int? customerId, int? sessionId, params (int ProductId, int Quantity)[] lines) =>
        new(lines.Select(l => new OrderLineDraft(l.ProductId, l.Quantity)).ToList(), customerId, sessionId);

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-1)]
    public async Task D02_quantity_outside_1_to_99_is_rejected(int quantity)
    {
        var id = await ProductAsync("Coke", 10m);

        Assert.False((await Create(Draft(null, null, (id, quantity)))).Success);
    }

    [Fact]
    public async Task D02_empty_order_is_rejected()
    {
        Assert.False((await Create(Draft(null, null))).Success);
    }

    [Fact]
    public async Task D02_inactive_product_is_rejected_by_name()
    {
        var id = await ProductAsync("Coke", 10m);
        await Svc(sp => sp.GetRequiredService<ProductService>().SetActiveAsync(id, false));

        var result = await Create(Draft(null, null, (id, 1)));

        Assert.False(result.Success);
        Assert.Contains("Coke", result.Error);
    }

    [Fact]
    public async Task D02_duplicate_product_lines_are_merged()
    {
        var id = await ProductAsync("Coke", 10m);

        var order = (await Create(Draft(null, null, (id, 2), (id, 3)))).Value!;

        var line = Assert.Single((await LoadOrderAsync(order.Id)).Lines);
        Assert.Equal(5, line.Quantity);
        Assert.Equal(50m, order.Total);
    }

    [Fact]
    public async Task D02_merged_quantity_above_99_is_rejected()
    {
        var id = await ProductAsync("Coke", 10m);

        Assert.False((await Create(Draft(null, null, (id, 60), (id, 60)))).Success);
    }

    [Fact]
    public async Task D02_attaching_to_an_ended_session_is_rejected()
    {
        var stationId = await StationAsync();
        var session = await PrepaidAsync(stationId, 60);
        await SvcAsync(s => s.StopAsync(session.Id));
        var id = await ProductAsync("Coke", 10m);

        Assert.False((await Create(Draft(null, session.Id, (id, 1)))).Success);
    }

    [Fact]
    public async Task D02_attaching_to_an_inactive_customer_is_rejected()
    {
        var customerId = await CustomerAsync();
        await Svc(sp => sp.GetRequiredService<Cafe.Server.Customers.CustomerService>().SetActiveAsync(customerId, false));
        var id = await ProductAsync("Coke", 10m);

        Assert.False((await Create(Draft(customerId, null, (id, 1)))).Success);
    }

    [Fact]
    public async Task D02_session_attached_order_copies_the_sessions_customer()
    {
        var customerId = await CustomerAsync();
        var session = await StartBalanceSessionAsync(customerId);
        var id = await ProductAsync("Coke", 10m);

        var order = (await Create(Draft(null, session.Id, (id, 1)))).Value!;

        Assert.Equal(customerId, order.CustomerId);
    }

    [Fact]
    public async Task D02_walk_in_order_is_allowed_and_zero_total_is_created_paid()
    {
        var free = await ProductAsync("Sample", 0m);
        var paid = await ProductAsync("Coke", 10m);

        var walkIn = (await Create(Draft(null, null, (paid, 1)))).Value!;
        var zero = (await Create(Draft(null, null, (free, 1)))).Value!;

        Assert.Null(walkIn.CustomerId);
        Assert.Null(walkIn.SessionId);
        Assert.Equal(OrderStatus.Open, walkIn.Status);
        Assert.Equal(OrderStatus.Paid, zero.Status);
    }

    [Fact]
    public async Task D02_price_change_after_creation_keeps_the_snapshot_and_a_new_order_uses_the_new_price()
    {
        var id = await ProductAsync("Coke", 10m);
        var first = (await Create(Draft(null, null, (id, 2)))).Value!;
        await Svc(sp => sp.GetRequiredService<ProductService>()
            .UpdateAsync(id, new ProductInput { Name = "Coke", Category = "Drinks", Price = 20m }));

        var second = (await Create(Draft(null, null, (id, 2)))).Value!;

        var oldLine = Assert.Single((await LoadOrderAsync(first.Id)).Lines);
        Assert.Equal(10m, oldLine.UnitPriceSnapshot);
        Assert.Equal(20m, oldLine.LineTotal);
        Assert.Equal(40m, Assert.Single((await LoadOrderAsync(second.Id)).Lines).LineTotal);
    }

    [Fact]
    public async Task D02_overlong_note_is_rejected()
    {
        var id = await ProductAsync("Coke", 10m);
        var draft = Draft(null, null, (id, 1)) with { Note = new string('x', 301) };

        Assert.False((await Create(draft)).Success);
    }

    [Fact]
    public async Task D03_open_order_without_allocations_can_be_voided()
    {
        var order = await OrderAsync(null, null, (await ProductAsync("Coke", 10m), 1));

        Assert.True((await Void(order.Id)).Success);
        Assert.Equal(OrderStatus.Voided, (await LoadOrderAsync(order.Id)).Status);
    }

    [Fact]
    public async Task D03_order_with_a_partial_payment_cannot_be_voided()
    {
        var order = await OrderAsync(null, null, (await ProductAsync("Coke", 10m), 5));
        var pay = await Svc(sp => sp.GetRequiredService<SettlementService>().SettleAsync(new SettleRequest(
            [new SettleTarget(AllocationTargetType.Order, order.Id)], [new TenderLine(PaymentMethod.Cash, 20m)], 50m)));
        Assert.True(pay.Success, pay.Error);

        var result = await Void(order.Id);

        Assert.False(result.Success);
        Assert.Contains("payment", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("settle the remainder", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OrderStatus.Open, (await LoadOrderAsync(order.Id)).Status);
    }

    [Fact]
    public async Task D03_paid_voided_and_unknown_orders_cannot_be_voided()
    {
        var order = await OrderAsync(null, null, (await ProductAsync("Coke", 10m), 1));
        await Svc(sp => sp.GetRequiredService<SettlementService>().SettleAsync(new SettleRequest(
            [new SettleTarget(AllocationTargetType.Order, order.Id)], [new TenderLine(PaymentMethod.Cash, 10m)], 10m)));
        var other = await OrderAsync(null, null, (await ProductAsync("Fanta", 10m), 1));
        await Void(other.Id);

        Assert.False((await Void(order.Id)).Success);
        Assert.False((await Void(other.Id)).Success);
        Assert.Equal(FailureKind.NotFound, (await Void(9999)).Kind);
    }

    [Fact]
    public async Task D03_voided_order_cannot_be_settled()
    {
        var order = await OrderAsync(null, null, (await ProductAsync("Coke", 10m), 1));
        await Void(order.Id);

        var pay = await Svc(sp => sp.GetRequiredService<SettlementService>().SettleAsync(new SettleRequest(
            [new SettleTarget(AllocationTargetType.Order, order.Id)], [new TenderLine(PaymentMethod.Cash, 10m)], 10m)));

        Assert.False(pay.Success);
    }

    [Fact]
    public async Task Queries_return_orders_with_lines_newest_first_and_per_session()
    {
        var stationId = await StationAsync();
        var session = await PrepaidAsync(stationId, 60);
        var id = await ProductAsync("Coke", 10m);
        var a = await OrderAsync(session.Id, null, (id, 1));
        Advance(TimeSpan.FromMinutes(1));
        var b = await OrderAsync(null, null, (id, 2));
        Advance(TimeSpan.FromMinutes(1));
        var c = await OrderAsync(session.Id, null, (id, 3));
        await Void(c.Id);

        var recent = await Svc(sp => sp.GetRequiredService<OrderService>().RecentOrdersAsync(50));
        var openForSession = await Svc(sp => sp.GetRequiredService<OrderService>().OrdersForSessionAsync(session.Id, openOnly: true));
        var allForSession = await Svc(sp => sp.GetRequiredService<OrderService>().OrdersForSessionAsync(session.Id, openOnly: false));
        var one = await Svc(sp => sp.GetRequiredService<OrderService>().GetAsync(b.Id));

        Assert.Equal([c.Id, b.Id, a.Id], recent.Select(o => o.Id));
        Assert.Equal([a.Id], openForSession.Select(o => o.Id));
        Assert.Equal([a.Id, c.Id], allForSession.Select(o => o.Id));
        Assert.Equal(2, Assert.Single(one!.Lines).Quantity);
        Assert.Null(await Svc(sp => sp.GetRequiredService<OrderService>().GetAsync(9999)));
    }

    private async Task<Session> StartBalanceSessionAsync(int customerId)
    {
        var stationId = await StationAsync("PC-09");
        var package = await PackageAsync("Hour", 60, 10m);
        await SellAsync(customerId, package);
        return await StartBalanceAsync(stationId, customerId, 30);
    }
}
