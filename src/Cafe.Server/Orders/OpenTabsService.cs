using System.Globalization;
using Cafe.Server.Data;
using Cafe.Server.Payments;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Orders;

/// <summary>One thing staff can settle together: a session (charge plus its orders), a customer's orders or a walk-in order.</summary>
public sealed record OpenTabGroup(
    string Key, string Header, IReadOnlyList<OutstandingItem> Items, decimal TotalOutstanding,
    DateTimeOffset OldestUtc, int? SessionId, int? CustomerId);

/// <summary>A session an order can be attached to.</summary>
public sealed record AttachableSession(int SessionId, string StationName, string? CustomerName, SessionStatus Status);

/// <summary>
/// Open tabs read model (POS-03, D-05). Lists what is still owed: Open orders with outstanding above zero and Ended
/// sessions with an unpaid charge that ended on or after Pos:BillingStartUtc (so pre-billing history never shows as
/// unpaid). Voided and paid orders never appear. Amounts are summed in memory because SQLite cannot SUM TEXT decimals.
/// </summary>
public class OpenTabsService(IDbContextFactory<CafeDbContext> dbFactory)
{
    public const string BillingStartKey = "Pos:BillingStartUtc";

    public async Task<IReadOnlyList<OpenTabGroup>> GetOpenTabsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var cutoff = await BillingStartAsync(db, cancellationToken);

        var orders = await db.Orders.AsNoTracking().Where(o => o.Status == OrderStatus.Open).ToListAsync(cancellationToken);
        var endedSessions = (await db.Sessions.AsNoTracking().Where(s => s.Status == SessionStatus.Ended)
                .Select(s => new { s.Id, s.ChargedAmount, s.EndedAtUtc }).ToListAsync(cancellationToken))
            .Where(s => s.ChargedAmount > 0m && s.EndedAtUtc is { } ended && ended >= cutoff)
            .ToList();

        var allocations = await LoadAllocationsAsync(db, orders.Select(o => o.Id).ToList(), endedSessions.Select(s => s.Id).ToList(), cancellationToken);
        decimal Paid(AllocationTargetType type, int id) => allocations.Where(a => a.TargetType == type && a.TargetId == id).Sum(a => a.Amount);

        var orderItems = orders
            .Select(o => (Order: o, Item: new OutstandingItem(
                AllocationTargetType.Order, o.Id, $"Order #{o.Id}", o.Total, Paid(AllocationTargetType.Order, o.Id),
                OutstandingCalculator.Remaining(o.Total, [Paid(AllocationTargetType.Order, o.Id)]))))
            .Where(x => x.Item.Outstanding > 0m)
            .ToList();
        var sessionItems = endedSessions
            .Select(s => (s.Id, s.EndedAtUtc, Paid: Paid(AllocationTargetType.Session, s.Id), s.ChargedAmount))
            .Where(x => OutstandingCalculator.Remaining(x.ChargedAmount, [x.Paid]) > 0m)
            .ToList();

        var sessionIds = orderItems.Where(x => x.Order.SessionId is not null).Select(x => x.Order.SessionId!.Value)
            .Concat(sessionItems.Select(x => x.Id)).Distinct().ToList();
        var sessions = (await db.Sessions.AsNoTracking().Include(s => s.Station).Include(s => s.Customer)
                .Where(s => sessionIds.Contains(s.Id)).ToListAsync(cancellationToken))
            .ToDictionary(s => s.Id);
        var customerIds = orderItems.Where(x => x.Order.SessionId is null && x.Order.CustomerId is not null)
            .Select(x => x.Order.CustomerId!.Value).Distinct().ToList();
        var customers = await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);

        var groups = new List<OpenTabGroup>();
        foreach (var sessionId in sessionIds)
        {
            var session = sessions[sessionId];
            var items = new List<OutstandingItem>();
            var oldest = DateTimeOffset.MaxValue;
            if (sessionItems.FirstOrDefault(x => x.Id == sessionId) is { Id: > 0 } unpaid)
            {
                items.Add(new OutstandingItem(
                    AllocationTargetType.Session, sessionId, session.Station.Name, unpaid.ChargedAmount, unpaid.Paid,
                    OutstandingCalculator.Remaining(unpaid.ChargedAmount, [unpaid.Paid])));
                oldest = unpaid.EndedAtUtc!.Value;
            }

            foreach (var x in orderItems.Where(x => x.Order.SessionId == sessionId).OrderBy(x => x.Order.Id))
            {
                items.Add(x.Item);
                oldest = oldest < x.Order.CreatedAtUtc ? oldest : x.Order.CreatedAtUtc;
            }

            var header = session.Customer is { } c ? $"{session.Station.Name} ({c.Name})" : session.Station.Name;
            if (session.Status != SessionStatus.Ended)
            {
                header += " - open session";
            }

            groups.Add(new OpenTabGroup($"session:{sessionId}", header, items, items.Sum(i => i.Outstanding), oldest, sessionId, session.CustomerId));
        }

        foreach (var byCustomer in orderItems.Where(x => x.Order.SessionId is null && x.Order.CustomerId is not null)
                     .GroupBy(x => x.Order.CustomerId!.Value))
        {
            var items = byCustomer.OrderBy(x => x.Order.Id).Select(x => x.Item).ToList();
            groups.Add(new OpenTabGroup(
                $"customer:{byCustomer.Key}", customers[byCustomer.Key].Name, items, items.Sum(i => i.Outstanding),
                byCustomer.Min(x => x.Order.CreatedAtUtc), null, byCustomer.Key));
        }

        foreach (var walkIn in orderItems.Where(x => x.Order.SessionId is null && x.Order.CustomerId is null))
        {
            groups.Add(new OpenTabGroup(
                $"order:{walkIn.Order.Id}", $"Walk-in order #{walkIn.Order.Id}", [walkIn.Item], walkIn.Item.Outstanding,
                walkIn.Order.CreatedAtUtc, null, null));
        }

        return groups.OrderBy(g => g.OldestUtc).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>Sessions that are not Ended: where a new order can be attached.</summary>
    public async Task<IReadOnlyList<AttachableSession>> ListAttachableSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Sessions.AsNoTracking().Include(s => s.Station).Include(s => s.Customer)
            .Where(s => s.Status != SessionStatus.Ended).ToListAsync(cancellationToken);
        return rows
            .OrderBy(s => s.Station.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Id)
            .Select(s => new AttachableSession(s.Id, s.Station.Name, s.Customer?.Name, s.Status))
            .ToList();
    }

    private static async Task<DateTimeOffset> BillingStartAsync(CafeDbContext db, CancellationToken ct)
    {
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == BillingStartKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return value is not null && DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    private static async Task<List<PaymentAllocation>> LoadAllocationsAsync(
        CafeDbContext db, List<int> orderIds, List<int> sessionIds, CancellationToken ct)
    {
        // One query for both kinds: filter by id range per type in SQL, exact match in memory.
        var rows = await db.PaymentAllocations.AsNoTracking()
            .Where(a => (a.TargetType == AllocationTargetType.Order && orderIds.Contains(a.TargetId))
                        || (a.TargetType == AllocationTargetType.Session && sessionIds.Contains(a.TargetId)))
            .ToListAsync(ct);
        return rows;
    }
}
