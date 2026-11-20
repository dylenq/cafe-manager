using Cafe.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Customers;

/// <summary>A balance bucket: StationTypeId null is the any-station-type bucket.</summary>
public sealed record BalanceBucket(int? StationTypeId, int Minutes);

/// <summary>
/// Balance math over the minute ledger. The ledger is the only source of truth: a bucket's balance is the SUM of
/// MinutesDelta. Mutating helpers only add rows to the caller's context; the caller saves (so a session and its
/// ledger rows commit together) and holds <see cref="BalanceLock"/> for debits.
/// </summary>
public class BalanceService(Microsoft.EntityFrameworkCore.IDbContextFactory<CafeDbContext> dbFactory)
{
    /// <summary>Minutes a customer can spend on a station of this type: type bucket plus any-type bucket, negatives ignored.</summary>
    public async Task<int> GetUsableMinutesAsync(int customerId, int stationTypeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await UsableMinutesAsync(db, customerId, stationTypeId, cancellationToken);
    }

    /// <summary>All non-zero buckets of a customer.</summary>
    public async Task<List<BalanceBucket>> GetBucketsAsync(int customerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CustomerMinuteLedger.AsNoTracking()
            .Where(l => l.CustomerId == customerId)
            .GroupBy(l => l.StationTypeId)
            .Select(g => new BalanceBucket(g.Key, g.Sum(l => l.MinutesDelta)))
            .Where(b => b.Minutes != 0)
            .ToListAsync(cancellationToken);
    }

    internal static async Task<int> BucketAsync(CafeDbContext db, int customerId, int? stationTypeId, CancellationToken ct) =>
        await db.CustomerMinuteLedger
            .Where(l => l.CustomerId == customerId && l.StationTypeId == stationTypeId)
            .SumAsync(l => l.MinutesDelta, ct);

    internal static async Task<int> UsableMinutesAsync(CafeDbContext db, int customerId, int stationTypeId, CancellationToken ct)
    {
        var typed = await BucketAsync(db, customerId, stationTypeId, ct);
        var any = await BucketAsync(db, customerId, null, ct);
        return Math.Max(0, typed) + Math.Max(0, any);
    }

    /// <summary>
    /// Adds SessionDebit rows (negative, one per non-zero slice) drawing the type-specific bucket first, then the
    /// any-type bucket. The caller must have checked the usable minutes under <see cref="BalanceLock"/>.
    /// </summary>
    internal static async Task<List<BalanceBucket>> DebitAsync(
        CafeDbContext db,
        int customerId,
        int stationTypeId,
        int minutes,
        DateTimeOffset now,
        Session? newSession,
        int? sessionId,
        CancellationToken ct)
    {
        var typed = Math.Max(0, await BucketAsync(db, customerId, stationTypeId, ct));
        var fromTyped = Math.Min(typed, minutes);
        var fromAny = minutes - fromTyped;
        var slices = new List<BalanceBucket>();
        if (fromTyped > 0)
        {
            slices.Add(new BalanceBucket(stationTypeId, fromTyped));
        }

        if (fromAny > 0)
        {
            slices.Add(new BalanceBucket(null, fromAny));
        }

        foreach (var slice in slices)
        {
            db.CustomerMinuteLedger.Add(new CustomerMinuteLedger
            {
                CustomerId = customerId,
                StationTypeId = slice.StationTypeId,
                MinutesDelta = -slice.Minutes,
                Reason = LedgerReason.SessionDebit,
                SessionId = sessionId,
                Session = newSession,
                AtUtc = now,
            });
        }

        return slices;
    }

    /// <summary>
    /// Refunds up to <paramref name="unusedMinutes"/> of a session's net-drawn minutes back to the buckets they came
    /// from, last-drawn bucket first. Returns the minutes refunded.
    /// </summary>
    internal static async Task<int> RefundUnusedAsync(CafeDbContext db, Session session, int unusedMinutes, DateTimeOffset now, CancellationToken ct = default)
    {
        if (unusedMinutes <= 0 || session.CustomerId is not { } customerId)
        {
            return 0;
        }

        var rows = await db.CustomerMinuteLedger.AsNoTracking()
            .Where(l => l.SessionId == session.Id && (l.Reason == LedgerReason.SessionDebit || l.Reason == LedgerReason.SessionRefund))
            .ToListAsync(ct);

        var buckets = rows
            .GroupBy(l => l.StationTypeId)
            .Select(g => new
            {
                StationTypeId = g.Key,
                NetDrawn = -g.Sum(l => l.MinutesDelta),
                LastDebitId = g.Where(l => l.Reason == LedgerReason.SessionDebit).Select(l => l.Id).DefaultIfEmpty(0).Max(),
            })
            .Where(b => b.NetDrawn > 0)
            .OrderByDescending(b => b.LastDebitId)
            .ToList();

        var remaining = unusedMinutes;
        var refunded = 0;
        foreach (var bucket in buckets)
        {
            if (remaining <= 0)
            {
                break;
            }

            var amount = Math.Min(bucket.NetDrawn, remaining);
            db.CustomerMinuteLedger.Add(new CustomerMinuteLedger
            {
                CustomerId = customerId,
                StationTypeId = bucket.StationTypeId,
                MinutesDelta = amount,
                Reason = LedgerReason.SessionRefund,
                SessionId = session.Id,
                AtUtc = now,
            });
            remaining -= amount;
            refunded += amount;
        }

        return refunded;
    }
}
