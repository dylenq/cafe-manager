using Cafe.Server.Data;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Payments;

/// <summary>
/// Outstanding amounts and settlement (POS-03). Allocations are the source of truth: outstanding = total minus the sum
/// of allocations to the target. A settlement writes the Payment rows, their allocations and the order Paid marks
/// with a single SaveChanges, so SQLite commits all of it or none.
/// </summary>
public class SettlementService(IDbContextFactory<CafeDbContext> dbFactory, TimeProvider time, SettlementLock settlementLock)
{
    public const string ChangedMessage = "Amounts changed — review the new total.";
    public const string OverpayMessage = "Payment is more than the amount due.";
    /// <summary>Same text as <see cref="OverpayMessage"/>; the name pages match on (D-04).</summary>
    public const string OverDue = OverpayMessage;
    public const string NothingToPay = "Nothing to pay.";
    public const decimal MaxLineAmount = 100_000_000m;
    public const string StopFirst = "Stop the session first.";
    private const string Gone = "One of the items no longer exists. Refresh and try again.";

    /// <summary>What each target currently owes. Reads only.</summary>
    public async Task<ServiceResult<IReadOnlyList<OutstandingItem>>> PreviewAsync(
        IReadOnlyList<SettleTarget> targets, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var loaded = await LoadAsync(db, targets, cancellationToken);
        if (!loaded.Success)
        {
            return loaded.Kind == FailureKind.NotFound
                ? ServiceResult<IReadOnlyList<OutstandingItem>>.NotFound(loaded.Error!)
                : ServiceResult<IReadOnlyList<OutstandingItem>>.Fail(loaded.Error!);
        }

        IReadOnlyList<OutstandingItem> items = loaded.Value!
            .Select(t => new OutstandingItem(t.Type, t.Id, t.Label, t.Total, t.Paid, t.Outstanding)).ToList();
        return ServiceResult<IReadOnlyList<OutstandingItem>>.Ok(items);
    }

    public async Task<ServiceResult<SettleResult>> SettleAsync(SettleRequest request, CancellationToken cancellationToken = default)
    {
        var tenderError = ValidateTenders(request.Tenders);
        if (tenderError is not null)
        {
            return ServiceResult<SettleResult>.Fail(tenderError, new Dictionary<string, string> { ["Tenders"] = tenderError });
        }

        using var _ = await settlementLock.AcquireAsync(cancellationToken);
        return await SettleLockedAsync(request, cancellationToken);
    }

    /// <summary>The body of <see cref="SettleAsync"/>; the caller already holds <see cref="SettlementLock"/> and validated the tenders.</summary>
    internal async Task<ServiceResult<SettleResult>> SettleLockedAsync(SettleRequest request, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var loaded = await LoadAsync(db, request.Targets, cancellationToken);
        if (!loaded.Success)
        {
            return loaded.Kind == FailureKind.NotFound
                ? ServiceResult<SettleResult>.NotFound(loaded.Error!)
                : ServiceResult<SettleResult>.Fail(loaded.Error!);
        }

        var owing = loaded.Value!.Where(t => t.Outstanding > 0m).ToList();
        var totalDue = owing.Sum(t => t.Outstanding);
        // The expected-due check comes first: a second circuit paying an item the first just paid sees the
        // changed-amounts message (D-05), not a bare "nothing to pay".
        if (totalDue != request.ExpectedDue)
        {
            return ServiceResult<SettleResult>.Fail(ChangedMessage);
        }

        if (totalDue <= 0m)
        {
            return ServiceResult<SettleResult>.Fail(NothingToPay);
        }

        var applied = request.Tenders.Sum(t => t.Amount);
        if (applied > totalDue)
        {
            return ServiceResult<SettleResult>.Fail(OverpayMessage, new Dictionary<string, string> { ["Tenders"] = OverpayMessage });
        }

        var plan = AllocationPlanner.Plan(
            owing.Select(t => new PlanTarget(t.Type, t.Id, t.Outstanding)).ToList(),
            request.Tenders.Select(t => t.Amount).ToList());

        var now = time.GetUtcNow();
        var payments = new List<Payment>();
        for (var i = 0; i < request.Tenders.Count; i++)
        {
            var tender = request.Tenders[i];
            var payment = new Payment
            {
                Amount = tender.Amount,
                Method = tender.Method,
                AtUtc = now,
                Note = request.Note,
                PrimaryPayment = i == 0 ? null : payments[0],
            };
            // Denormalised convenience when the checkout pays exactly one target of a kind; allocations are the truth.
            payment.SessionId = SoleId(owing, AllocationTargetType.Session);
            payment.OrderId = SoleId(owing, AllocationTargetType.Order);
            payment.PackageSaleId = SoleId(owing, AllocationTargetType.PackageSale);
            foreach (var part in plan[i])
            {
                payment.Allocations.Add(new PaymentAllocation { TargetType = part.Type, TargetId = part.Id, Amount = part.Amount });
            }

            payments.Add(payment);
            db.Payments.Add(payment);
        }

        var paidOrders = new List<int>();
        foreach (var target in owing.Where(t => t.Type == AllocationTargetType.Order))
        {
            var allocatedNow = plan.SelectMany(p => p).Where(a => a.Type == target.Type && a.Id == target.Id).Sum(a => a.Amount);
            if (allocatedNow >= target.Outstanding && target.Order is { } order)
            {
                order.Status = OrderStatus.Paid;
                paidOrders.Add(order.Id);
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // One SaveChanges is one transaction: nothing was written.
            return ServiceResult<SettleResult>.Fail("The payment could not be saved, so nothing was recorded. Try again.");
        }

        var change = request.Tenders.Where(t => t.Method == PaymentMethod.Cash && t.Tendered is not null).Sum(t => t.Tendered!.Value - t.Amount);
        return ServiceResult<SettleResult>.Ok(new SettleResult(
            payments.Select(p => p.Id).ToList(), payments[0].Id, totalDue, applied, totalDue - applied, change, paidOrders));
    }

    private static int? SoleId(List<LoadedTarget> owing, AllocationTargetType type)
    {
        var of = owing.Where(t => t.Type == type).ToList();
        return of.Count == 1 ? of[0].Id : null;
    }

    /// <summary>Tender rules shared with the package sale: positive amounts, cash-only tendered, tendered at least the amount.</summary>
    internal static string? ValidateTenders(IReadOnlyList<TenderLine> tenders)
    {
        if (tenders.Count == 0)
        {
            return "Add at least one payment.";
        }

        foreach (var tender in tenders)
        {
            if (tender.Amount <= 0m)
            {
                return "Each payment must be more than zero.";
            }

            if (tender.Amount > MaxLineAmount || tender.Tendered > MaxLineAmount)
            {
                return "That payment amount is too large.";
            }

            if (tender.Tendered is { } tendered)
            {
                if (tender.Method != PaymentMethod.Cash)
                {
                    return "Only cash can have a tendered amount.";
                }

                if (tendered < tender.Amount)
                {
                    return "The cash handed over cannot be less than the amount paid.";
                }
            }
        }

        return null;
    }

    /// <summary>Loads each distinct target fresh with its allocations. Orders are tracked so callers can mark them Paid.</summary>
    internal static async Task<ServiceResult<List<LoadedTarget>>> LoadAsync(
        CafeDbContext db, IReadOnlyList<SettleTarget> targets, CancellationToken ct)
    {
        var distinct = targets.Distinct().ToList();
        var allocations = new List<PaymentAllocation>();
        foreach (var group in distinct.GroupBy(t => t.Type))
        {
            var type = group.Key;
            var ids = group.Select(t => t.Id).ToList();
            allocations.AddRange(await db.PaymentAllocations.AsNoTracking()
                .Where(a => a.TargetType == type && ids.Contains(a.TargetId)).ToListAsync(ct));
        }

        var result = new List<LoadedTarget>();
        foreach (var target in distinct)
        {
            decimal total;
            string label;
            Order? order = null;
            switch (target.Type)
            {
                case AllocationTargetType.Session:
                    var session = await db.Sessions.AsNoTracking().Include(s => s.Station)
                        .FirstOrDefaultAsync(s => s.Id == target.Id, ct);
                    if (session is null)
                    {
                        return ServiceResult<List<LoadedTarget>>.NotFound(Gone);
                    }

                    if (session.Status != SessionStatus.Ended)
                    {
                        return ServiceResult<List<LoadedTarget>>.Fail(StopFirst);
                    }

                    total = session.ChargedAmount;
                    label = session.Station.Name;
                    break;
                case AllocationTargetType.Order:
                    order = await db.Orders.FirstOrDefaultAsync(o => o.Id == target.Id, ct);
                    if (order is null)
                    {
                        return ServiceResult<List<LoadedTarget>>.NotFound(Gone);
                    }

                    if (order.Status == OrderStatus.Voided)
                    {
                        return ServiceResult<List<LoadedTarget>>.Fail($"Order #{order.Id} was voided.");
                    }

                    total = order.Total;
                    label = $"Order #{order.Id}";
                    break;
                default:
                    var sale = await db.PackageSales.AsNoTracking().Include(s => s.TimePackage)
                        .FirstOrDefaultAsync(s => s.Id == target.Id, ct);
                    if (sale is null)
                    {
                        return ServiceResult<List<LoadedTarget>>.NotFound(Gone);
                    }

                    total = sale.Price;
                    label = sale.TimePackage.Name;
                    break;
            }

            var paid = allocations.Where(a => a.TargetType == target.Type && a.TargetId == target.Id).Sum(a => a.Amount);
            var outstanding = OutstandingCalculator.Remaining(total, [paid]);
            result.Add(new LoadedTarget(target.Type, target.Id, label, total, paid, outstanding, order));
        }

        return ServiceResult<List<LoadedTarget>>.Ok(result);
    }

    internal sealed record LoadedTarget(
        AllocationTargetType Type, int Id, string Label, decimal Total, decimal Paid, decimal Outstanding, Order? Order);
}
