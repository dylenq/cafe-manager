using System.Globalization;
using Cafe.Server.Data;

namespace Cafe.Server.Receipts;

/// <summary>Renders a <see cref="ReceiptDocument"/> to ESC/POS bytes (D-08). Pure.</summary>
public sealed class EscPosReceiptRenderer
{
    public const int DefaultColumns = 42;

    public byte[] Render(ReceiptDocument doc, int columns = DefaultColumns)
    {
        var layout = new ReceiptTextLayout(columns);
        var enc = new EscPosEncoder().Initialize();

        void Lines(IEnumerable<string> lines)
        {
            foreach (var line in lines)
            {
                enc.Text(line).LineFeed();
            }
        }

        enc.Align(EscPosAlign.Center).Bold(true).DoubleHeight(true);
        // The magnified mode can double the glyph width, so the name wraps at half the columns.
        Lines(ReceiptTextLayout.Wrap(doc.Header.CafeName, Math.Max(1, columns / 2)));
        enc.DoubleHeight(false).Bold(false);
        foreach (var line in doc.Header.Lines)
        {
            Lines(ReceiptTextLayout.Wrap(line, columns));
        }

        enc.Align(EscPosAlign.Left);
        enc.Text(layout.Separator()).LineFeed();
        Lines(layout.Row("No. " + doc.Number, string.Empty));
        Lines(layout.Row(doc.LocalTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), string.Empty));
        enc.Text(layout.Separator()).LineFeed();

        foreach (var target in doc.Targets)
        {
            enc.Bold(true);
            Lines(target.Items.Count == 0
                ? layout.Row(target.Title, ReceiptTextLayout.FormatAmount(target.Total))
                : ReceiptTextLayout.Wrap(target.Title, columns));
            enc.Bold(false);
            if (!string.IsNullOrWhiteSpace(target.Detail))
            {
                Lines(ReceiptTextLayout.Wrap(target.Detail, columns));
            }

            foreach (var item in target.Items)
            {
                Lines(layout.ItemRows(item.Quantity, item.Name, item.UnitPrice, item.Amount));
            }
        }

        enc.Text(layout.Separator()).LineFeed();
        enc.Bold(true);
        Lines(layout.Pair("Total", ReceiptTextLayout.FormatAmount(doc.Subtotal)));
        enc.Bold(false);
        foreach (var payment in doc.Payments)
        {
            Lines(layout.Pair(
                payment.Method == PaymentMethod.Card ? "Card" : "Cash",
                ReceiptTextLayout.FormatAmount(payment.Amount)));
        }

        if (doc.Payments.Count > 1 || doc.Paid != doc.Subtotal)
        {
            Lines(layout.Pair("Paid", ReceiptTextLayout.FormatAmount(doc.Paid)));
        }

        if (doc.Change is { } change)
        {
            Lines(layout.Pair("Change", ReceiptTextLayout.FormatAmount(change)));
        }

        if (doc.Remaining > 0m)
        {
            Lines(layout.Pair("Remaining", ReceiptTextLayout.FormatAmount(doc.Remaining)));
        }

        enc.Text(layout.Separator()).LineFeed();
        enc.Align(EscPosAlign.Center);
        Lines(ReceiptTextLayout.Wrap(doc.Header.Footer, columns));
        enc.Align(EscPosAlign.Left).LineFeed(3).Cut();
        return enc.ToArray();
    }
}
