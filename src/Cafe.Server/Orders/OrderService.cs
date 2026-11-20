using Cafe.Server.Data;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Orders;

public class OrderService(IDbContextFactory<CafeDbContext> dbFactory, TimeProvider time)
{
    public const int MaxQuantity = 99;

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

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ids = merged.Select(l => l.ProductId).ToList();
        var products = await db.Products.Where(p => p.IsActive && ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        if (merged.Any(l => !products.ContainsKey(l.ProductId)))
        {
            return ServiceResult<Order>.Fail("One of the products is no longer on sale. Refresh and try again.");
        }

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
}
