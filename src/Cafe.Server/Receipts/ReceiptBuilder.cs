namespace Cafe.Server.Receipts;

/// <summary>Turns a <see cref="ReceiptSource"/> into a <see cref="ReceiptDocument"/>. Pure: no database, no clock.</summary>
public static class ReceiptBuilder
{
    public static ReceiptDocument Build(ReceiptSource source) => new(
        Number: source.RootPaymentId.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
        LocalTime: TimeZoneInfo.ConvertTime(source.AtUtc, source.Zone).DateTime,
        Header: source.Header,
        Targets: source.Targets,
        Subtotal: source.Targets.Sum(t => t.Total),
        Payments: source.Payments,
        Paid: source.Payments.Sum(p => p.Amount),
        Change: source.ChangeGiven,
        Remaining: source.Targets.Sum(t => t.Remaining),
        RootPaymentId: source.RootPaymentId);
}
