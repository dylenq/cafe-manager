using Cafe.Server.Data;
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
    public async Task<ReportData> GetAsync(ReportRange range, CancellationToken ct = default)
    {
        var zone = pricing.TimeZone;
        var (startUtc, endUtc) = range.ToUtcBounds(zone);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var payments = (await db.Payments.AsNoTracking().Include(p => p.Allocations).ToListAsync(ct))
            .Where(p => p.AtUtc >= startUtc && p.AtUtc < endUtc)
            .Select(p => new PaymentFact(
                p.Id, p.Method, p.AtUtc,
                p.Allocations.Select(a => new AllocationFact(a.TargetType, a.TargetId, a.Amount)).ToList()))
            .ToList();

        return Build(new ReportInput(payments), range, zone);
    }

    /// <summary>Pure aggregation of already-loaded facts into report sections.</summary>
    public static ReportData Build(ReportInput input, ReportRange range, TimeZoneInfo zone)
    {
        var (startUtc, endUtc) = range.ToUtcBounds(zone);
        var inRange = input.Payments.Where(p => p.AtUtc >= startUtc && p.AtUtc < endUtc).ToList();

        var byDay = new Dictionary<DateOnly, (decimal Cash, decimal Card)>();
        foreach (var payment in inRange)
        {
            var day = LocalDate(payment.AtUtc, zone);
            var amount = payment.Allocations.Sum(a => a.Amount);
            byDay.TryGetValue(day, out var current);
            byDay[day] = payment.Method == PaymentMethod.Cash
                ? (current.Cash + amount, current.Card)
                : (current.Cash, current.Card + amount);
        }

        var days = new List<DayRow>();
        for (var date = range.From; date <= range.To; date = date.AddDays(1))
        {
            byDay.TryGetValue(date, out var v);
            days.Add(new DayRow(date, v.Cash + v.Card, v.Cash, v.Card));
        }

        var cash = days.Sum(d => d.Cash);
        var card = days.Sum(d => d.Card);
        return new ReportData(range, new ReportSummary(cash + card, cash, card), days);
    }

    internal static DateOnly LocalDate(DateTimeOffset utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);
}
