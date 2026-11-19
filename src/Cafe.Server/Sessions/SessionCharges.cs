using Cafe.Server.Data;
using Cafe.Server.Pricing;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Sessions;

/// <summary>Open-ended charge computation shared by stop, the stop preview and the dashboard estimate.</summary>
public class SessionCharges(PricingConfigService pricing)
{
    /// <summary>
    /// Prices every segment at its own station's type base rate. An open segment counts up to <paramref name="now"/>,
    /// so the result is both the running estimate and the amount a stop at <paramref name="now"/> would bill.
    /// </summary>
    public async Task<decimal> OpenEndedAsync(
        CafeDbContext db,
        IEnumerable<SessionSegment> segments,
        DateTimeOffset now,
        IReadOnlyList<RateRule>? rules = null,
        CancellationToken cancellationToken = default)
    {
        var ordered = segments.OrderBy(s => s.StartUtc).ThenBy(s => s.Id).ToList();
        if (ordered.Count == 0)
        {
            return 0m;
        }

        var ids = ordered.Select(s => s.StationId).Distinct().ToList();
        var stations = (await db.Stations.AsNoTracking()
                .Include(s => s.StationType)
                .Where(s => ids.Contains(s.Id))
                .ToListAsync(cancellationToken))
            .ToDictionary(s => s.Id);

        var intervals = ordered
            .Where(s => stations.ContainsKey(s.StationId))
            .Select(s =>
            {
                var station = stations[s.StationId];
                return new PricedInterval(s.StartUtc, s.EndUtc ?? now, station.StationTypeId, station.StationType.HourlyRate);
            })
            .ToList();

        rules ??= await pricing.GetEnabledRulesAsync(db, cancellationToken);
        return PricingEngine.Price(new PricingRequest(intervals, rules, pricing.TimeZone)).Total;
    }
}
