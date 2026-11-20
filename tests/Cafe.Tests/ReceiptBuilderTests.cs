using Cafe.Server.Data;
using Cafe.Server.Receipts;

namespace Cafe.Tests;

public class ReceiptBuilderTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly ReceiptHeader Header = new("Cafe", [], "Thank you!");

    private static ReceiptTarget Session(decimal total = 6000m, decimal applied = 6000m, decimal remaining = 0m) =>
        new(ReceiptTargetKind.Session, "PC-01", "60 min, Prepaid", total, applied, remaining, []);

    private static ReceiptTarget OrderTarget(string name = "Iced Coffee", decimal remaining = 0m) =>
        new(ReceiptTargetKind.Order, "Order #1", null, 50000m, 50000m - remaining, remaining,
            [new ReceiptItem(name, 2, 25000m, 50000m)]);

    private static ReceiptDocument Build(
        int root = 7, TimeZoneInfo? zone = null, decimal? change = null, IReadOnlyList<ReceiptTarget>? targets = null,
        IReadOnlyList<ReceiptPayment>? payments = null) =>
        ReceiptBuilder.Build(new ReceiptSource(
            root, Noon, zone ?? TimeZoneInfo.Utc, targets ?? [Session(), OrderTarget()],
            payments ?? [new ReceiptPayment(PaymentMethod.Cash, 56000m)], change, Header));

    [Fact]
    public void Subtotal_is_the_sum_of_all_targets()
    {
        var doc = Build();

        Assert.Equal(56000m, doc.Subtotal);
        Assert.Equal(56000m, doc.Paid);
        Assert.Equal(0m, doc.Remaining);
    }

    [Fact]
    public void Split_payment_keeps_input_order_and_sums_to_paid()
    {
        var doc = Build(payments: [new ReceiptPayment(PaymentMethod.Cash, 30000m), new ReceiptPayment(PaymentMethod.Card, 26000m)]);

        Assert.Equal([PaymentMethod.Cash, PaymentMethod.Card], doc.Payments.Select(p => p.Method));
        Assert.Equal(56000m, doc.Paid);
        Assert.Equal(0m, doc.Remaining);
    }

    [Fact]
    public void Partial_payment_reports_the_remaining_amount()
    {
        var doc = Build(
            targets: [Session(), OrderTarget(remaining: 36000m)],
            payments: [new ReceiptPayment(PaymentMethod.Cash, 20000m)]);

        Assert.Equal(20000m, doc.Paid);
        Assert.Equal(36000m, doc.Remaining);
        Assert.Equal(56000m, doc.Subtotal);
    }

    [Fact]
    public void Change_is_only_carried_when_known()
    {
        Assert.Null(Build().Change);
        Assert.Equal(44000m, Build(change: 44000m).Change);
    }

    [Fact]
    public void Local_time_uses_the_given_zone()
    {
        var plus7 = TimeZoneInfo.CreateCustomTimeZone("UTC+7", TimeSpan.FromHours(7), "UTC+7", "UTC+7");

        Assert.Equal(new DateTime(2026, 11, 20, 19, 0, 0), Build(zone: plus7).LocalTime);
        Assert.Equal(new DateTime(2026, 11, 20, 12, 0, 0), Build().LocalTime);
    }

    [Fact]
    public void Number_is_the_root_payment_id_zero_padded_to_six()
    {
        Assert.Equal("000007", Build(root: 7).Number);
        Assert.Equal("1234567", Build(root: 1234567).Number);
    }

    [Fact]
    public void Item_lines_and_non_ascii_names_are_preserved()
    {
        var doc = Build(targets: [OrderTarget("Cà phê sữa đá")]);

        var item = Assert.Single(Assert.Single(doc.Targets).Items);
        Assert.Equal("Cà phê sữa đá", item.Name);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(25000m, item.UnitPrice);
        Assert.Equal(50000m, item.Amount);
    }
}
