using Cafe.Server.Data;

namespace Cafe.Server.Payments;

/// <summary>One amount typed into the payment dialog: what was entered for a method.</summary>
public sealed record TenderEntry(PaymentMethod Method, decimal Entered);

/// <summary>
/// The dialog's result. <see cref="Lines"/> are ready for SettleAsync (applied amounts, cash carries the handed-over
/// amount). <see cref="CardClamped"/> says a card entry was above what was still due and was cut down.
/// </summary>
public sealed record TenderResult(
    IReadOnlyList<TenderLine> Lines, decimal Applied, decimal Remaining, decimal Change, bool CardClamped);

/// <summary>
/// Pure tender / change / remaining maths (D-04). Cash may overshoot: the overshoot is change and is never stored.
/// Card never overshoots: it is clamped to what is still due. Zero and negative entries are ignored.
/// </summary>
public static class TenderCalculator
{
    public static TenderResult Calculate(decimal due, IReadOnlyList<TenderEntry> entries)
    {
        var remaining = Math.Max(0m, due);
        var lines = new List<TenderLine>();
        var change = 0m;
        var clamped = false;
        foreach (var entry in entries)
        {
            if (entry.Entered <= 0m)
            {
                continue;
            }

            var applied = Math.Min(entry.Entered, remaining);
            if (entry.Method == PaymentMethod.Card && entry.Entered > applied)
            {
                clamped = true;
            }

            if (applied <= 0m)
            {
                continue;
            }

            remaining -= applied;
            if (entry.Method == PaymentMethod.Cash)
            {
                lines.Add(new TenderLine(PaymentMethod.Cash, applied, entry.Entered));
                change += entry.Entered - applied;
            }
            else
            {
                lines.Add(new TenderLine(PaymentMethod.Card, applied));
            }
        }

        var totalApplied = lines.Sum(l => l.Amount);
        return new TenderResult(lines, totalApplied, remaining, change, clamped);
    }
}
