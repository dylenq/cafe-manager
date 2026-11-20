using System.Globalization;
using Cafe.Server.Data;
using Cafe.Server.Orders;
using Cafe.Server.Pricing;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Reports;

/// <summary>
/// Revenue reports on a cash basis: revenue is the sum of payment allocations, dated by Payment.AtUtc in cafe local
/// time. SQLite stores decimals as TEXT and DateTimeOffset as binary, so rows are loaded and filtered/summed in memory
/// (the data set is tiny: under 15 stations, one cafe).
/// </summary>
public class ReportService(IDbContextFactory<CafeDbContext> dbFactory, PricingConfigService pricing)
{
    public const string UncategorisedCategory = "Uncategorised";
    public const string UnknownStationType = "Unknown";
    public const string TimePackagesRow = "Time packages";

    public async Task<ReportData> GetAsync(ReportRange range, CancellationToken ct = default)
    {
        var zone = pricing.TimeZone;
        var (startUtc, endUtc) = range.ToUtcBounds(zone);
        bool InRange(DateTimeOffset at) => at >= startUtc && at < endUtc;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Payments in range (with allocations), plus every allocation ever made (needed for the unpaid figure).
        var payments = (await db.Payments.AsNoTracking().Include(p => p.Allocations).ToListAsync(ct))
            .Where(p => InRange(p.AtUtc)).ToList();
        var allocatedAllTime = (await db.PaymentAllocations.AsNoTracking()
                .Select(a => new { a.TargetType, a.TargetId, a.Amount }).ToListAsync(ct))
            .GroupBy(a => (a.TargetType, a.TargetId))
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        decimal Allocated(AllocationTargetType type, int id) => allocatedAllTime.GetValueOrDefault((type, id));

        var billingStart = await BillingStartAsync(db, ct);

        // Sessions: ended in the range (count, hours, unpaid) or paid in the range (station-type grouping).
        var sessionIdsPaid = payments.SelectMany(p => p.Allocations)
            .Where(a => a.TargetType == AllocationTargetType.Session).Select(a => a.TargetId).ToHashSet();
        var endedSessions = (await db.Sessions.AsNoTracking()
                .Where(s => s.Status == SessionStatus.Ended)
                .Select(s => new { s.Id, s.EndedAtUtc, s.ChargedAmount }).ToListAsync(ct))
            .Where(s => s.EndedAtUtc is not null)
            .ToList();
        var inRangeSessions = endedSessions.Where(s => InRange(s.EndedAtUtc!.Value)).ToList();
        var neededSessionIds = inRangeSessions.Select(s => s.Id).Concat(sessionIdsPaid).ToHashSet();
        var segments = (await db.SessionSegments.AsNoTracking()
                .Where(s => neededSessionIds.Contains(s.SessionId)).ToListAsync(ct))
            .GroupBy(s => s.SessionId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.StartUtc).ThenBy(s => s.Id).ToList());
        var stationTypeByStation = (await db.Stations.AsNoTracking().Include(s => s.StationType).ToListAsync(ct))
            .ToDictionary(s => s.Id, s => s.StationType.Name);
        string TypeOf(int sessionId) =>
            segments.TryGetValue(sessionId, out var list) && list.Count > 0
                && stationTypeByStation.TryGetValue(list[0].StationId, out var name)
                ? name
                : UnknownStationType;

        var sessionFacts = inRangeSessions.Select(s => new SessionFact(
            s.Id, TypeOf(s.Id),
            (long)(segments.GetValueOrDefault(s.Id)?.Where(g => g.EndUtc is not null).Sum(g => (g.EndUtc!.Value - g.StartUtc).TotalSeconds) ?? 0d),
            s.EndedAtUtc!.Value)).ToList();
        var sessionTypes = neededSessionIds.ToDictionary(id => id, TypeOf);

        // Orders: never voided ones. Needed with lines when created in range or paid in range.
        var orderIdsPaid = payments.SelectMany(p => p.Allocations)
            .Where(a => a.TargetType == AllocationTargetType.Order).Select(a => a.TargetId).ToHashSet();
        var orders = (await db.Orders.AsNoTracking().Where(o => o.Status != OrderStatus.Voided)
                .Select(o => new { o.Id, o.CreatedAtUtc, o.Total }).ToListAsync(ct))
            .Where(o => InRange(o.CreatedAtUtc) || orderIdsPaid.Contains(o.Id)).ToList();
        var orderIds = orders.Select(o => o.Id).ToHashSet();
        var lines = (await db.OrderLines.AsNoTracking().Where(l => orderIds.Contains(l.OrderId)).ToListAsync(ct))
            .GroupBy(l => l.OrderId).ToDictionary(g => g.Key, g => g.OrderBy(l => l.Id).ToList());
        var categories = (await db.Products.AsNoTracking().Select(p => new { p.Id, p.Category }).ToListAsync(ct))
            .ToDictionary(p => p.Id, p => p.Category);
        var orderFacts = orders.Select(o => new OrderFact(o.Id, o.Total,
            (lines.GetValueOrDefault(o.Id) ?? []).Select(l => new OrderLineFact(
                l.ProductNameSnapshot,
                categories.TryGetValue(l.ProductId, out var c) && !string.IsNullOrWhiteSpace(c) ? c : UncategorisedCategory,
                l.Quantity, l.LineTotal)).ToList())).ToList();

        var packageSales = (await db.PackageSales.AsNoTracking().Select(p => new { p.Id, p.Price, p.AtUtc }).ToListAsync(ct))
            .Where(p => InRange(p.AtUtc)).ToList();

        // Charged (unpaid): the not-yet-allocated part of everything billed inside the range.
        var charges = new List<ChargeFact>();
        charges.AddRange(inRangeSessions.Where(s => s.EndedAtUtc!.Value >= billingStart).Select(s => new ChargeFact(
            AllocationTargetType.Session, s.Id, s.EndedAtUtc!.Value, s.ChargedAmount, Allocated(AllocationTargetType.Session, s.Id))));
        charges.AddRange(orders.Where(o => InRange(o.CreatedAtUtc)).Select(o => new ChargeFact(
            AllocationTargetType.Order, o.Id, o.CreatedAtUtc, o.Total, Allocated(AllocationTargetType.Order, o.Id))));
        charges.AddRange(packageSales.Select(p => new ChargeFact(
            AllocationTargetType.PackageSale, p.Id, p.AtUtc, p.Price, Allocated(AllocationTargetType.PackageSale, p.Id))));

        var paymentFacts = payments.Select(p => new PaymentFact(
            p.Id, p.Method, p.AtUtc,
            p.Allocations.Select(a => new AllocationFact(a.TargetType, a.TargetId, a.Amount)).ToList())).ToList();

        return Build(new ReportInput(paymentFacts, sessionFacts, orderFacts, charges, sessionTypes), range, zone);
    }

    /// <summary>
    /// Pure aggregation of already-loaded facts into report sections (cash basis).
    /// Product revenue: an order's allocations (in range) are shared across its lines pro rata to LineTotal, with the
    /// rounding remainder going to the last line so the lines sum exactly to the allocated amount. Quantity sold is
    /// attributed the same way (Quantity x allocated / Total), so it is whole once an order is fully paid.
    /// </summary>
    public static ReportData Build(ReportInput input, ReportRange range, TimeZoneInfo zone)
    {
        var (startUtc, endUtc) = range.ToUtcBounds(zone);
        bool InRange(DateTimeOffset at) => at >= startUtc && at < endUtc;
        var payments = input.Payments.Where(p => InRange(p.AtUtc)).ToList();

        // Revenue by day and by payment method.
        var byDay = new Dictionary<DateOnly, (decimal Cash, decimal Card)>();
        var byMethod = Enum.GetValues<PaymentMethod>().ToDictionary(m => m, _ => 0m);
        foreach (var payment in payments)
        {
            var day = LocalDate(payment.AtUtc, zone);
            var amount = payment.Allocations.Sum(a => a.Amount);
            byDay.TryGetValue(day, out var current);
            byDay[day] = payment.Method == PaymentMethod.Cash
                ? (current.Cash + amount, current.Card)
                : (current.Cash, current.Card + amount);
            byMethod[payment.Method] += amount;
        }

        var days = new List<DayRow>();
        for (var date = range.From; date <= range.To; date = date.AddDays(1))
        {
            byDay.TryGetValue(date, out var v);
            days.Add(new DayRow(date, v.Cash + v.Card, v.Cash, v.Card));
        }

        var weeks = new List<WeekRow>();
        for (var monday = ReportRange.WeekStart(range.From); monday <= range.To; monday = monday.AddDays(7))
        {
            var start = monday;
            weeks.Add(new WeekRow(start, ReportRange.IsoWeekLabel(start),
                days.Where(d => ReportRange.WeekStart(d.Date) == start).Sum(d => d.Collected)));
        }

        var cash = days.Sum(d => d.Cash);
        var card = days.Sum(d => d.Card);
        var allocations = payments.SelectMany(p => p.Allocations).ToList();

        // Station type: session allocations by the first segment's type, hours by sessions ended in range.
        var sessions = (input.Sessions ?? []).Where(s => InRange(s.EndedAtUtc)).ToList();
        var typeNames = input.SessionStationTypes ?? new Dictionary<int, string>();
        var typeCollected = allocations.Where(a => a.TargetType == AllocationTargetType.Session)
            .GroupBy(a => typeNames.GetValueOrDefault(a.TargetId, UnknownStationType))
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        var typeHours = sessions.GroupBy(s => s.StationTypeName)
            .ToDictionary(g => g.Key, g => R2(g.Sum(s => s.ActiveSeconds) / 3600m));
        var stationTypes = typeCollected.Keys.Union(typeHours.Keys)
            .Select(n => new StationTypeRow(n, typeCollected.GetValueOrDefault(n), typeHours.GetValueOrDefault(n)))
            .OrderByDescending(r => r.Collected).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var packages = allocations.Where(a => a.TargetType == AllocationTargetType.PackageSale).Sum(a => a.Amount);
        if (packages != 0m)
        {
            stationTypes.Add(new StationTypeRow(TimePackagesRow, packages, 0m));
        }

        // Products and categories from order allocations.
        var orderIndex = (input.Orders ?? []).ToDictionary(o => o.Id);
        var productAcc = new Dictionary<(string Name, string Category), (decimal Qty, decimal Revenue)>();
        foreach (var orderGroup in allocations.Where(a => a.TargetType == AllocationTargetType.Order).GroupBy(a => a.TargetId))
        {
            if (!orderIndex.TryGetValue(orderGroup.Key, out var order) || order.Total == 0m || order.Lines.Count == 0)
            {
                continue;
            }

            var allocated = orderGroup.Sum(a => a.Amount);
            var remaining = allocated;
            for (var i = 0; i < order.Lines.Count; i++)
            {
                var line = order.Lines[i];
                var share = i == order.Lines.Count - 1 ? remaining : R2(allocated * line.LineTotal / order.Total);
                remaining -= share;
                var qty = R2(line.Quantity * allocated / order.Total);
                var key = (line.ProductName, line.Category);
                productAcc.TryGetValue(key, out var cur);
                productAcc[key] = (cur.Qty + qty, cur.Revenue + share);
            }
        }

        var products = productAcc
            .Select(kv => new ProductRow(kv.Key.Name, kv.Key.Category, kv.Value.Qty, kv.Value.Revenue))
            .OrderByDescending(p => p.Revenue).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var categoryRows = products.GroupBy(p => p.Category)
            .Select(g => new CategoryRow(g.Key, g.Sum(p => p.Quantity), g.Sum(p => p.Revenue)))
            .OrderByDescending(c => c.Revenue).ThenBy(c => c.Category, StringComparer.OrdinalIgnoreCase).ToList();

        var methods = byMethod.OrderBy(kv => (int)kv.Key).Select(kv => new MethodRow(kv.Key, kv.Value)).ToList();

        var totalSeconds = sessions.Sum(s => s.ActiveSeconds);
        var hours = R2(totalSeconds / 3600m);
        var average = sessions.Count == 0 ? 0m : R2(totalSeconds / 60m / sessions.Count);
        var unpaid = (input.Charges ?? []).Where(c => InRange(c.AtUtc)).Sum(c => Math.Max(0m, c.Total - c.AllocatedAllTime));

        return new ReportData(
            range,
            new ReportSummary(cash + card, cash, card, sessions.Count, hours, average, unpaid),
            days, weeks, stationTypes, products, categoryRows, methods);
    }

    internal static DateOnly LocalDate(DateTimeOffset utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);

    private static decimal R2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static async Task<DateTimeOffset> BillingStartAsync(CafeDbContext db, CancellationToken ct)
    {
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == OpenTabsService.BillingStartKey)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        return value is not null && DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }
}
