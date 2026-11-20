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
