using Cafe.Server.Data;

namespace Cafe.Server.Receipts;

public enum ReceiptTargetKind
{
    Session,
    Order,
    Package,
}

public sealed record ReceiptItem(string Name, int Quantity, decimal UnitPrice, decimal Amount);

/// <summary>One thing a receipt covers. AppliedNow is what this checkout paid; Remaining is what still owes afterwards.</summary>
public sealed record ReceiptTarget(
    ReceiptTargetKind Kind,
    string Title,
    string? Detail,
    decimal Total,
    decimal AppliedNow,
    decimal Remaining,
    IReadOnlyList<ReceiptItem> Items);

public sealed record ReceiptPayment(PaymentMethod Method, decimal Amount);

public sealed record ReceiptHeader(string CafeName, IReadOnlyList<string> Lines, string Footer);

/// <summary>Everything the pure builder needs: no database, no clock.</summary>
public sealed record ReceiptSource(
    int RootPaymentId,
    DateTimeOffset AtUtc,
    TimeZoneInfo Zone,
    IReadOnlyList<ReceiptTarget> Targets,
    IReadOnlyList<ReceiptPayment> Payments,
    decimal? ChangeGiven,
    ReceiptHeader Header);

public sealed record ReceiptDocument(
    string Number,
    DateTime LocalTime,
    ReceiptHeader Header,
    IReadOnlyList<ReceiptTarget> Targets,
    decimal Subtotal,
    IReadOnlyList<ReceiptPayment> Payments,
    decimal Paid,
    decimal? Change,
    decimal Remaining,
    int RootPaymentId = 0);
