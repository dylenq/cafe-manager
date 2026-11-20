using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Payments;

/// <summary>Stops a session and settles its charge plus its tab orders (POS-03, "Stop &amp; settle").</summary>
public class CheckoutService(
    SessionService sessions, SettlementService settlement, IDbContextFactory<CafeDbContext> dbFactory)
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

        List<int> orderIds;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            orderIds = await db.Orders.AsNoTracking()
                .Where(o => o.SessionId == sessionId && o.Status == OrderStatus.Open)
                .OrderBy(o => o.Id).Select(o => o.Id).ToListAsync(cancellationToken);
        }

        IReadOnlyList<OutstandingItem> owing = [];
        if (orderIds.Count > 0)
        {
            var preview = await settlement.PreviewAsync(
                orderIds.Select(id => new SettleTarget(AllocationTargetType.Order, id)).ToList(), cancellationToken);
            if (!preview.Success)
            {
                return ServiceResult<StopSettleQuote>.Fail(preview.Error!);
            }

            owing = preview.Value!.Where(i => i.Outstanding > 0m).ToList();
        }

        return ServiceResult<StopSettleQuote>.Ok(new StopSettleQuote(
            quote.Value!, owing, quote.Value!.Amount + owing.Sum(i => i.Outstanding)));
    }

    /// <summary>
    /// Stops the session (refused with SessionService.ChangedMessage when the quote is stale) then pays the session
    /// charge and every open order on it. With no tenders nothing is paid ("Stop, pay later"). If the stop succeeded
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

        var settled = await settlement.SettleAsync(new SettleRequest(targets, tenders, expectedDue), cancellationToken);
        return ServiceResult<CheckoutResult>.Ok(settled.Success
            ? new CheckoutResult(charge, settled.Value, null)
            : new CheckoutResult(charge, null, settled.Error));
    }
}
