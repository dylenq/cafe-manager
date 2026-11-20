using Cafe.Server.Data;
using Cafe.Server.Sessions;

namespace Cafe.Server.Payments;

/// <summary>One thing being paid for: a session charge, an order or a package sale.</summary>
public sealed record SettleTarget(AllocationTargetType Type, int Id);

/// <summary>
/// One payment line. <paramref name="Amount"/> is what is APPLIED to the bill; <paramref name="Tendered"/> is the cash
/// handed over (Cash only), so change = Tendered - Amount. Change is never stored.
/// </summary>
public sealed record TenderLine(PaymentMethod Method, decimal Amount, decimal? Tendered = null);

public sealed record SettleRequest(
    IReadOnlyList<SettleTarget> Targets,
    IReadOnlyList<TenderLine> Tenders,
    decimal ExpectedDue,
    string? Note = null);

public sealed record SettleResult(
    IReadOnlyList<int> PaymentIds,
    int ReceiptPaymentId,
    decimal TotalDue,
    decimal Applied,
    decimal Remaining,
    decimal Change,
    IReadOnlyList<int> PaidOrderIds);

public sealed record OutstandingItem(
    AllocationTargetType Type,
    int Id,
    string Label,
    decimal Total,
    decimal Paid,
    decimal Outstanding);

/// <summary>
/// Outcome of stopping a session and settling it. <see cref="Settlement"/> is null when nothing was paid;
/// <see cref="PaymentError"/> is set when the stop succeeded but the payment was refused (items stay outstanding).
/// </summary>
public sealed record CheckoutResult(decimal SessionCharge, SettleResult? Settlement, string? PaymentError);

/// <summary>A target with its outstanding amount, as the planner sees it.</summary>
public sealed record PlanTarget(AllocationTargetType Type, int Id, decimal Outstanding);

/// <summary>Part of one payment applied to one target.</summary>
public sealed record PlannedAllocation(AllocationTargetType Type, int Id, decimal Amount);

/// <summary>
/// What "Stop &amp; settle" shows before confirming: the stop quote (with its version), the session's open orders that
/// still owe, and the total due (stop charge plus open orders' outstanding).
/// </summary>
public sealed record StopSettleQuote(StopQuote Stop, IReadOnlyList<OutstandingItem> OpenOrders, decimal TotalDue);
