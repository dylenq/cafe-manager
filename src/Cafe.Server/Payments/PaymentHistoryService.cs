using Cafe.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Payments;

/// <summary>One receipt (a root payment and any split payments sharing it) as the history lists show it.</summary>
public sealed record ReceiptSummary(int RootPaymentId, DateTimeOffset AtUtc, decimal Total, string Methods, string Label);

/// <summary>Recent receipts and a customer's receipts, for reprint (D-08). Reads only.</summary>
public class PaymentHistoryService(IDbContextFactory<CafeDbContext> dbFactory)
{
    public async Task<IReadOnlyList<ReceiptSummary>> RecentAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Ids grow with time and SQLite cannot ORDER BY a DateTimeOffset, so the newest roots are the highest ids.
        var rootIds = await db.Payments.AsNoTracking().Where(p => p.PrimaryPaymentId == null)
            .OrderByDescending(p => p.Id).Select(p => p.Id).Take(take).ToListAsync(cancellationToken);
        return await BuildAsync(db, rootIds, cancellationToken);
    }

    /// <summary>Receipts that paid for the customer's orders, package sales or sessions.</summary>
    public async Task<IReadOnlyList<ReceiptSummary>> ForCustomerAsync(int customerId, int take = 50, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var orderIds = await db.Orders.AsNoTracking().Where(o => o.CustomerId == customerId).Select(o => o.Id).ToListAsync(cancellationToken);
        var saleIds = await db.PackageSales.AsNoTracking().Where(s => s.CustomerId == customerId).Select(s => s.Id).ToListAsync(cancellationToken);
        var sessionIds = await db.Sessions.AsNoTracking().Where(s => s.CustomerId == customerId).Select(s => s.Id).ToListAsync(cancellationToken);

        var paymentIds = await db.PaymentAllocations.AsNoTracking()
            .Where(a => (a.TargetType == AllocationTargetType.Order && orderIds.Contains(a.TargetId))
                        || (a.TargetType == AllocationTargetType.PackageSale && saleIds.Contains(a.TargetId))
                        || (a.TargetType == AllocationTargetType.Session && sessionIds.Contains(a.TargetId)))
            .Select(a => a.PaymentId).Distinct().ToListAsync(cancellationToken);
        var rootIds = (await db.Payments.AsNoTracking().Where(p => paymentIds.Contains(p.Id))
                .Select(p => p.PrimaryPaymentId ?? p.Id).ToListAsync(cancellationToken))
            .Distinct().OrderByDescending(id => id).Take(take).ToList();
        return await BuildAsync(db, rootIds, cancellationToken);
    }

    private static async Task<IReadOnlyList<ReceiptSummary>> BuildAsync(CafeDbContext db, List<int> rootIds, CancellationToken ct)
    {
        if (rootIds.Count == 0)
        {
            return [];
        }

        var payments = await db.Payments.AsNoTracking().Include(p => p.Allocations)
            .Where(p => rootIds.Contains(p.Id) || (p.PrimaryPaymentId != null && rootIds.Contains(p.PrimaryPaymentId.Value)))
            .ToListAsync(ct);
        var allocations = payments.SelectMany(p => p.Allocations).ToList();
        var labels = await LabelsAsync(db, allocations, ct);

        var result = new List<ReceiptSummary>();
        foreach (var rootId in rootIds)
        {
            var group = payments.Where(p => p.Id == rootId || p.PrimaryPaymentId == rootId).OrderBy(p => p.Id).ToList();
            if (group.Count == 0)
            {
                continue;
            }

            var methods = string.Join(" + ", group.GroupBy(p => p.Method)
                .Select(g => $"{g.Key} {g.Sum(p => p.Amount):#,##0.##}"));
            var targets = group.SelectMany(p => p.Allocations).Select(a => (a.TargetType, a.TargetId)).Distinct()
                .Select(t => labels.GetValueOrDefault(t, "Payment")).ToList();
            var label = targets.Count switch
            {
                0 => "Payment",
                <= 2 => string.Join(", ", targets),
                _ => $"{targets[0]}, {targets[1]} +{targets.Count - 2} more",
            };
            result.Add(new ReceiptSummary(rootId, group[0].AtUtc, group.Sum(p => p.Amount), methods, label));
        }

        return result.OrderByDescending(r => r.AtUtc).ThenByDescending(r => r.RootPaymentId).ToList();
    }

    private static async Task<Dictionary<(AllocationTargetType, int), string>> LabelsAsync(
        CafeDbContext db, List<PaymentAllocation> allocations, CancellationToken ct)
    {
        var labels = new Dictionary<(AllocationTargetType, int), string>();
        foreach (var id in allocations.Where(a => a.TargetType == AllocationTargetType.Order).Select(a => a.TargetId).Distinct())
        {
            labels[(AllocationTargetType.Order, id)] = $"Order #{id}";
        }

        var sessionIds = allocations.Where(a => a.TargetType == AllocationTargetType.Session).Select(a => a.TargetId).Distinct().ToList();
        foreach (var s in await db.Sessions.AsNoTracking().Include(s => s.Station).Where(s => sessionIds.Contains(s.Id)).ToListAsync(ct))
        {
            labels[(AllocationTargetType.Session, s.Id)] = s.Station.Name;
        }

        var saleIds = allocations.Where(a => a.TargetType == AllocationTargetType.PackageSale).Select(a => a.TargetId).Distinct().ToList();
        foreach (var s in await db.PackageSales.AsNoTracking().Include(s => s.TimePackage).Where(s => saleIds.Contains(s.Id)).ToListAsync(ct))
        {
            labels[(AllocationTargetType.PackageSale, s.Id)] = s.TimePackage.Name;
        }

        return labels;
    }
}
