using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Payments;

/// <summary>Stops a session and settles its charge plus its tab orders (POS-03, "Stop &amp; settle").</summary>
public class CheckoutService(
    SessionService sessions, SettlementService settlement, IDbContextFactory<CafeDbContext> dbFactory, SettlementLock settlementLock)
{
    /// <summary>
    /// Reads what stopping and settling would cost right now: the stop quote plus the session's open orders that still
    /// owe. Reads only. An ended or missing session fails exactly as <see cref="SessionService.QuoteStopAsync"/> does.
    /// </summary>
    public async Task<ServiceResult<StopSettleQuote>> QuoteStopAndSettleAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        var quote = await sessions.QuoteStopAsync(sessionId, cancellationToken);
        if (!quote.Success)
        {
            return quote.Kind == FailureKind.NotFound
                ? ServiceResult<StopSettleQuote>.NotFound(quote.Error!)
                : ServiceResult<StopSettleQuote>.Fail(quote.Error!);
        }

        var owing = await OwingOrdersAsync(sessionId, cancellationToken);
        if (!owing.Success)
        {
            return ServiceResult<StopSettleQuote>.Fail(owing.Error!);
        }

        return ServiceResult<StopSettleQuote>.Ok(new StopSettleQuote(
            quote.Value!, owing.Value!, quote.Value!.Amount + owing.Value!.Sum(i => i.Outstanding)));
    }

    /// <summary>
    /// True when a fresh quote differs from the one shown only because an open-ended session's elapsed minutes raised the
    /// session charge: same session version, same open orders and amounts, a different session charge. The UI then
    /// updates the total and asks for one more confirmation instead of failing. Prepaid and balance sessions never
    /// qualify (their charge only moves with a version bump), so those stay strict.
    /// </summary>
    public static bool OnlyElapsedChargeChanged(StopSettleQuote shown, StopSettleQuote current, bool openEnded) =>
        openEnded
        && shown.Stop.SessionId == current.Stop.SessionId
        && shown.Stop.Version == current.Stop.Version
        && shown.Stop.Amount != current.Stop.Amount
        && shown.OpenOrders.Count == current.OpenOrders.Count
        && shown.OpenOrders.Zip(current.OpenOrders).All(p => p.First.Type == p.Second.Type && p.First.Id == p.Second.Id && p.First.Outstanding == p.Second.Outstanding);

    /// <summary>
    /// Stops the session then pays the session charge and every open order on it. Under <see cref="SettlementLock"/> and
    /// before anything is stopped the full due (session charge plus open orders) is compared with
    /// <paramref name="expectedDue"/>: a mismatch returns <see cref="SettlementService.ChangedMessage"/> and the session
    /// keeps running. A stale session version or charge is refused with SessionService.ChangedMessage. With no tenders nothing is paid ("Stop, pay later"). If the stop succeeded
    /// but the payment is refused the result is still a success with <see cref="CheckoutResult.PaymentError"/> set:
    /// the session is already ended and the items stay outstanding, so no money is lost.
    /// </summary>
    public async Task<ServiceResult<CheckoutResult>> StopAndSettleAsync(
        int sessionId,
        int expectedVersion,
        decimal expectedSessionCharge,
        IReadOnlyList<TenderLine> tenders,
        decimal expectedDue,
        CancellationToken cancellationToken = default)
    {
        var tenderError = tenders.Count == 0 ? null : SettlementService.ValidateTenders(tenders);
        if (tenderError is not null)
        {
            return ServiceResult<CheckoutResult>.Fail(tenderError, new Dictionary<string, string> { ["Tenders"] = tenderError });
        }

        using var _ = await settlementLock.AcquireAsync(cancellationToken);

        // Full-due guard before stopping: an order added, voided or paid since the quote does not bump the session version.
        var current = await QuoteStopAndSettleAsync(sessionId, cancellationToken);
        if (!current.Success)
        {
            return current.Kind == FailureKind.NotFound
                ? ServiceResult<CheckoutResult>.NotFound(current.Error!)
                : ServiceResult<CheckoutResult>.Fail(current.Error!);
        }

        if (current.Value!.TotalDue != expectedDue)
        {
            return ServiceResult<CheckoutResult>.Fail(SettlementService.ChangedMessage);
        }

        var stopped = await sessions.StopAsync(sessionId, expectedVersion, expectedSessionCharge, cancellationToken);
        if (!stopped.Success)
        {
            return stopped.Kind == FailureKind.NotFound
                ? ServiceResult<CheckoutResult>.NotFound(stopped.Error!)
                : ServiceResult<CheckoutResult>.Fail(stopped.Error!);
        }

        var charge = stopped.Value;
        var targets = new List<SettleTarget>();
        if (charge > 0m)
        {
            targets.Add(new SettleTarget(AllocationTargetType.Session, sessionId));
        }

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var orderIds = await db.Orders.AsNoTracking()
                .Where(o => o.SessionId == sessionId && o.Status == OrderStatus.Open)
                .OrderBy(o => o.Id).Select(o => o.Id).ToListAsync(cancellationToken);
            targets.AddRange(orderIds.Select(id => new SettleTarget(AllocationTargetType.Order, id)));
        }

        if (targets.Count == 0 || tenders.Count == 0)
        {
            return ServiceResult<CheckoutResult>.Ok(new CheckoutResult(charge, null, null));
        }

        var settled = await settlement.SettleLockedAsync(new SettleRequest(targets, tenders, expectedDue), cancellationToken);
        return ServiceResult<CheckoutResult>.Ok(settled.Success
            ? new CheckoutResult(charge, settled.Value, null)
            : new CheckoutResult(charge, null, settled.Error));
    }

    private async Task<ServiceResult<IReadOnlyList<OutstandingItem>>> OwingOrdersAsync(int sessionId, CancellationToken cancellationToken)
    {
        List<int> orderIds;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            orderIds = await db.Orders.AsNoTracking()
                .Where(o => o.SessionId == sessionId && o.Status == OrderStatus.Open)
                .OrderBy(o => o.Id).Select(o => o.Id).ToListAsync(cancellationToken);
        }

        if (orderIds.Count == 0)
        {
            return ServiceResult<IReadOnlyList<OutstandingItem>>.Ok([]);
        }

        var preview = await settlement.PreviewAsync(
            orderIds.Select(id => new SettleTarget(AllocationTargetType.Order, id)).ToList(), cancellationToken);
        return preview.Success
            ? ServiceResult<IReadOnlyList<OutstandingItem>>.Ok(preview.Value!.Where(i => i.Outstanding > 0m).ToList())
            : ServiceResult<IReadOnlyList<OutstandingItem>>.Fail(preview.Error!);
    }
}
