using Cafe.Server.Data;
using Cafe.Server.Payments;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Orders;

public class OrderService(IDbContextFactory<CafeDbContext> dbFactory, TimeProvider time, SettlementLock settlementLock)
{
    public const int MaxQuantity = 99;
    public const int MaxNoteLength = 300;

    /// <summary>Rings up an Open order, snapshotting product names and prices. A zero-total order is created Paid.</summary>
    public async Task<ServiceResult<Order>> CreateAsync(OrderDraft draft, CancellationToken cancellationToken = default)
    {
        if (draft.Lines.Count == 0)
        {
            return ServiceResult<Order>.Fail("Add at least one item.");
        }

        if (draft.Lines.Any(l => l.Quantity is < 1 or > MaxQuantity))
        {
            return ServiceResult<Order>.Fail($"Quantity must be between 1 and {MaxQuantity}.");
        }

        var merged = draft.Lines.GroupBy(l => l.ProductId).Select(g => (ProductId: g.Key, Quantity: g.Sum(l => l.Quantity))).ToList();
        if (merged.Any(l => l.Quantity > MaxQuantity))
        {
            return ServiceResult<Order>.Fail($"Quantity must be between 1 and {MaxQuantity}.");
        }

        if (draft.Note is { } rawNote && rawNote.Trim().Length > MaxNoteLength)
        {
            return ServiceResult<Order>.Fail($"Note can be at most {MaxNoteLength} characters.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ids = merged.Select(l => l.ProductId).ToList();
        var found = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        if (merged.Any(l => !found.ContainsKey(l.ProductId)))
        {
            return ServiceResult<Order>.Fail("One of the products no longer exists. Refresh and try again.");
        }

        if (found.Values.FirstOrDefault(p => !p.IsActive) is { } inactive)
        {
            return ServiceResult<Order>.Fail($"\"{inactive.Name}\" is no longer on sale. Remove it and try again.");
        }

        var products = found;

        var customerId = draft.CustomerId;
        if (draft.SessionId is { } sessionId)
        {
            var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
            if (session is null || session.Status == SessionStatus.Ended)
            {
                return ServiceResult<Order>.Fail("That session is no longer running. Refresh and try again.");
            }

            customerId ??= session.CustomerId;
        }

        if (draft.CustomerId is { } requested
            && !await db.Customers.AnyAsync(c => c.Id == requested && c.IsActive, cancellationToken))
        {
            return ServiceResult<Order>.Fail("That customer is not available.");
        }

        var order = new Order
        {
            CreatedAtUtc = time.GetUtcNow(),
            CustomerId = customerId,
            SessionId = draft.SessionId,
            Note = InputValidation.Clean(draft.Note),
        };
        foreach (var (productId, quantity) in merged)
        {
            var product = products[productId];
            order.Lines.Add(new OrderLine
            {
                ProductId = productId,
                ProductNameSnapshot = product.Name,
                UnitPriceSnapshot = product.Price,
                Quantity = quantity,
                LineTotal = product.Price * quantity,
            });
        }

        order.Total = order.Lines.Sum(l => l.LineTotal);
        order.Status = order.Total == 0m ? OrderStatus.Paid : OrderStatus.Open;
        db.Orders.Add(order);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ServiceResult<Order>.Fail("The order could not be saved. Try again.");
        }

        return ServiceResult<Order>.Ok(order);
    }

    /// <summary>
    /// Voids an Open order (D-02). An order with any allocation is NOT voidable: staff settle the remainder, because
    /// refunds are out of scope and voiding would lose the money trail. Serialised with settlement by SettlementLock.
    /// </summary>
    public async Task<ServiceResult> VoidAsync(int orderId, CancellationToken cancellationToken = default)
    {
        using var _ = await settlementLock.AcquireAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null)
        {
            return ServiceResult.NotFound("That order no longer exists. Refresh the list.");
        }

        switch (order.Status)
        {
            case OrderStatus.Voided:
                return ServiceResult.Fail("That order is already voided.");
            case OrderStatus.Paid:
                return ServiceResult.Fail("A paid order cannot be voided.");
        }

        if (await db.PaymentAllocations.AnyAsync(a => a.TargetType == AllocationTargetType.Order && a.TargetId == orderId, cancellationToken))
        {
            return ServiceResult.Fail("This order already has a payment, so it cannot be voided. Settle the remainder instead.");
        }

        order.Status = OrderStatus.Voided;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ServiceResult.Fail("The order could not be voided. Try again.");
        }

        return ServiceResult.Ok();
    }

    public async Task<Order?> GetAsync(int orderId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Orders.AsNoTracking().Include(o => o.Lines).Include(o => o.Customer).Include(o => o.Session).ThenInclude(s => s!.Station)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
    }

    /// <summary>Newest first, every status.</summary>
    public async Task<List<Order>> RecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Orders.AsNoTracking().Include(o => o.Lines).Include(o => o.Customer).ToListAsync(cancellationToken);
        // Sorted in memory: SQLite cannot order DateTimeOffset natively.
        return rows.OrderByDescending(o => o.CreatedAtUtc).ThenByDescending(o => o.Id).Take(take).ToList();
    }

    public async Task<List<Order>> OrdersForSessionAsync(int sessionId, bool openOnly, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Orders.AsNoTracking().Include(o => o.Lines)
            .Where(o => o.SessionId == sessionId && (!openOnly || o.Status == OrderStatus.Open)).ToListAsync(cancellationToken);
        return rows.OrderBy(o => o.Id).ToList();
    }
}
