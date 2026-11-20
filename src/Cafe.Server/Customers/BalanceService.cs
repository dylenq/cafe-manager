using Cafe.Server.Data;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cafe.Server.Customers;

/// <summary>A balance bucket: StationTypeId null is the any-station-type bucket.</summary>
public sealed record BalanceBucket(int? StationTypeId, int Minutes);

/// <summary>
/// Balance math over the minute ledger. The ledger is the only source of truth: a bucket's balance is the SUM of
/// MinutesDelta. Mutating helpers only add rows to the caller's context; the caller saves (so a session and its
/// ledger rows commit together) and holds <see cref="BalanceLock"/> for debits.
/// </summary>
public class BalanceService(
    IDbContextFactory<CafeDbContext> dbFactory, TimeProvider time, BalanceLock balanceLock, ILogger<BalanceService>? logger = null)
{
    /// <summary>Minutes a customer can spend on a station of this type: type bucket plus any-type bucket, negatives ignored.</summary>
    public async Task<int> GetUsableMinutesAsync(int customerId, int stationTypeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await UsableMinutesAsync(db, customerId, stationTypeId, cancellationToken);
    }

    /// <summary>Usable minutes on a specific station (looks up its station type); 0 when the station is unknown.</summary>
    public async Task<int> GetUsableMinutesForStationAsync(int customerId, int stationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var typeId = await db.Stations.AsNoTracking().Where(s => s.Id == stationId).Select(s => (int?)s.StationTypeId).FirstOrDefaultAsync(cancellationToken);
        return typeId is { } id ? await UsableMinutesAsync(db, customerId, id, cancellationToken) : 0;
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

    /// <summary>Balance per bucket (non-zero only, any-type first) plus the total.</summary>
    public async Task<BalanceSummary> GetBalanceAsync(int customerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await SummaryAsync(db, customerId, cancellationToken);
    }

    /// <summary>Shared bucket sum used by the balance summary and the customer detail read model.</summary>
    internal static async Task<BalanceSummary> SummaryAsync(CafeDbContext db, int customerId, CancellationToken ct)
    {
        var sums = await db.CustomerMinuteLedger.AsNoTracking()
            .Where(l => l.CustomerId == customerId)
            .GroupBy(l => l.StationTypeId)
            .Select(g => new { StationTypeId = g.Key, Minutes = g.Sum(l => l.MinutesDelta) })
            .Where(b => b.Minutes != 0)
            .ToListAsync(ct);
        if (sums.Count == 0)
        {
            return BalanceSummary.Empty;
        }

        var names = await db.StationTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var rows = sums
            .Select(b => new BalanceBucketRow(b.StationTypeId, b.StationTypeId is { } id ? names.GetValueOrDefault(id) : null, b.Minutes))
            .OrderBy(b => b.StationTypeId is null ? 0 : 1)
            .ThenBy(b => b.StationTypeName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(b => b.StationTypeId)
            .ToList();
        return new BalanceSummary(rows, rows.Sum(r => r.Minutes));
    }

    public const int MaxAdjustmentMinutes = 100000;
    public const int MaxNoteLength = 300;

    /// <summary>
    /// Staff correction: adds one ManualAdjustment ledger row. A negative delta may not exceed the bucket's current
    /// minutes, so a bucket never goes below zero (T-05-06). Inactive customers may be adjusted. Runs under
    /// <see cref="BalanceLock"/> so it cannot race a debit or a sale.
    /// </summary>
    public async Task<ServiceResult<CustomerMinuteLedger>> AdjustAsync(
        int customerId, int? stationTypeId, int minutesDelta, string? note, CancellationToken cancellationToken = default)
    {
        var cleaned = note?.Trim() is { Length: > 0 } t ? t : null;
        if (cleaned is null)
        {
            return Field("Note", "A note is required so the adjustment can be audited.");
        }

        if (cleaned.Length > MaxNoteLength)
        {
            return Field("Note", $"Note must be {MaxNoteLength} characters or fewer.");
        }

        if (minutesDelta == 0)
        {
            return Field("MinutesDelta", "Enter a number of minutes other than zero.");
        }

        if (Math.Abs((long)minutesDelta) > MaxAdjustmentMinutes)
        {
            return Field("MinutesDelta", $"Adjust by at most {MaxAdjustmentMinutes} minutes at a time.");
        }

        using var _ = await balanceLock.AcquireAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Customers.AnyAsync(c => c.Id == customerId, cancellationToken))
        {
            return ServiceResult<CustomerMinuteLedger>.NotFound(CustomerService.Gone);
        }

        if (stationTypeId is { } typeId && !await db.StationTypes.AnyAsync(t => t.Id == typeId, cancellationToken))
        {
            return Field("StationTypeId", "That station type no longer exists.");
        }

        if (minutesDelta < 0)
        {
            var current = await BucketAsync(db, customerId, stationTypeId, cancellationToken);
            if (current + minutesDelta < 0)
            {
                var message = $"That bucket has only {Math.Max(0, current)} min, so it cannot be reduced by {-minutesDelta}.";
                return Field("MinutesDelta", message);
            }
        }

        var row = new CustomerMinuteLedger
        {
            CustomerId = customerId,
            StationTypeId = stationTypeId,
            MinutesDelta = minutesDelta,
            Reason = LedgerReason.ManualAdjustment,
            Note = cleaned,
            AtUtc = time.GetUtcNow(),
        };
        db.CustomerMinuteLedger.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger?.LogError(ex, "Balance adjustment could not be saved (customer {CustomerId}).", customerId);
            return ServiceResult<CustomerMinuteLedger>.Fail("The adjustment could not be saved. Try again.");
        }

        return ServiceResult<CustomerMinuteLedger>.Ok(row);
    }

    private static ServiceResult<CustomerMinuteLedger> Field(string field, string message) =>
        ServiceResult<CustomerMinuteLedger>.Fail(message, new Dictionary<string, string> { [field] = message });

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

    /// <summary>Minutes this session net-drew from each bucket (debits minus refunds), buckets with a positive net only.</summary>
    internal static async Task<List<BalanceBucket>> NetDrawnByBucketAsync(CafeDbContext db, int sessionId, CancellationToken ct)
    {
        var rows = await db.CustomerMinuteLedger.AsNoTracking()
            .Where(l => l.SessionId == sessionId && (l.Reason == LedgerReason.SessionDebit || l.Reason == LedgerReason.SessionRefund))
            .Select(l => new { l.StationTypeId, l.MinutesDelta })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.StationTypeId)
            .Select(g => new BalanceBucket(g.Key, -g.Sum(r => r.MinutesDelta)))
            .Where(b => b.Minutes > 0)
            .ToList();
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
