using System.Globalization;

namespace Cafe.Server.Receipts;

/// <summary>Fixed-width text layout for a thermal receipt (42 columns for 80mm paper, 32 for 58mm). Pure.</summary>
public sealed class ReceiptTextLayout(int columns)
{
    public int Columns { get; } = columns;

    /// <summary>Amount as plain number text (no currency symbol) so the printer never needs a glyph outside CP437.</summary>
    public static string FormatAmount(decimal value) =>
        value.ToString("N" + CultureInfo.CurrentCulture.NumberFormat.CurrencyDecimalDigits, CultureInfo.CurrentCulture);

    public string Separator() => new('-', Columns);

    /// <summary>Left text and a right-aligned amount; the left text wraps and the amount sits on the last line.</summary>
    public IReadOnlyList<string> Row(string left, string right)
    {
        left = EscPosEncoder.ToPrintable(left);
        right = EscPosEncoder.ToPrintable(right);
        if (right.Length == 0)
        {
            return Wrap(left, Columns);
        }

        var room = Columns - right.Length - 1;
        if (room < 4)
        {
            // The amount alone is nearly the whole line: left text first, amount on its own right-aligned line.
            var lines = Wrap(left, Columns).ToList();
            lines.Add(right.Length >= Columns ? right[..Columns] : right.PadLeft(Columns));
            return lines;
        }

        var wrapped = Wrap(left, room).ToList();
        if (wrapped.Count == 0)
        {
            wrapped.Add(string.Empty);
        }

        var last = wrapped[^1];
        wrapped[^1] = last.PadRight(Columns - right.Length) + right;
        return wrapped;
    }

    public IReadOnlyList<string> Pair(string label, string value) => Row(label, value);

    /// <summary>"2 x Iced Coffee @ 25,000" with the amount right-aligned.</summary>
    public IReadOnlyList<string> ItemRows(int quantity, string name, decimal unitPrice, decimal amount)
    {
        var left = string.Create(CultureInfo.InvariantCulture, $"{quantity} x {name}");
        if (quantity > 1)
        {
            left += " @ " + FormatAmount(unitPrice);
        }

        return Row(left, FormatAmount(amount));
    }

    public string Center(string text, int? width = null)
    {
        var w = width ?? Columns;
        text = EscPosEncoder.ToPrintable(text);
        if (text.Length >= w)
        {
            return text[..w];
        }

        var space = w - text.Length;
        var left = space / 2;
        return new string(' ', left) + text + new string(' ', space - left);
    }

    /// <summary>Word wrap; words longer than the width are hard-broken. Every line is at most width characters.</summary>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        text = EscPosEncoder.ToPrintable(text);
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var rawWord in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = rawWord;
            while (word.Length > width)
            {
                if (current.Length > 0)
                {
                    lines.Add(current);
                    current = string.Empty;
                }

                lines.Add(word[..width]);
                word = word[width..];
            }

            if (current.Length == 0)
            {
                current = word;
            }
            else if (current.Length + 1 + word.Length <= width)
            {
                current += " " + word;
            }
            else
            {
                lines.Add(current);
                current = word;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines;
    }
}
