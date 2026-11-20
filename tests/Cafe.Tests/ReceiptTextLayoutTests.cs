using Cafe.Server.Receipts;

namespace Cafe.Tests;

public class ReceiptTextLayoutTests
{
    [Fact]
    public void Row_pads_to_the_exact_width_with_the_amount_right_aligned_at_42_columns()
    {
        var lines = new ReceiptTextLayout(42).Row("Latte x2", "50,000");

        var line = Assert.Single(lines);
        Assert.Equal(42, line.Length);
        Assert.StartsWith("Latte x2", line);
        Assert.EndsWith("50,000", line);
    }

    [Fact]
    public void A_long_item_name_wraps_and_the_amount_sits_on_the_last_line_at_42_columns()
    {
        var name = "Extra Large Iced Vietnamese Coffee With Sweet Condensed Milk";
        Assert.Equal(60, name.Length);

        var lines = new ReceiptTextLayout(42).Row(name, "50,000");

        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 42));
        Assert.EndsWith("50,000", lines[^1]);
        Assert.Equal(42, lines[^1].Length);
    }

    [Fact]
    public void A_long_item_name_wraps_at_32_columns_and_no_line_exceeds_the_width()
    {
        var name = "Extra Large Iced Vietnamese Coffee With Sweet Condensed Milk";

        var lines = new ReceiptTextLayout(32).Row(name, "150,000");

        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 32, $"'{l}' is {l.Length}"));
        Assert.EndsWith("150,000", lines[^1]);
    }

    [Fact]
    public void A_single_word_longer_than_the_width_is_hard_broken()
    {
        var lines = ReceiptTextLayout.Wrap(new string('x', 70), 32);

        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.True(l.Length <= 32));
        Assert.Equal(70, lines.Sum(l => l.Length));
    }

    [Fact]
    public void Text_outside_cp437_is_measured_after_replacement()
    {
        // Each emoji prints as one '?', so the row is still exactly the column width.
        var lines = new ReceiptTextLayout(32).Row("\U0001F600\U0001F600 Cafe 漢", "10,000");

        var line = Assert.Single(lines);
        Assert.Equal(32, line.Length);
        Assert.StartsWith("?? Cafe ?", line);
    }

    [Fact]
    public void Center_pads_equally_with_the_left_getting_the_floor()
    {
        var centered = new ReceiptTextLayout(42).Center("CAFE");

        Assert.Equal(42, centered.Length);
        Assert.Equal(new string(' ', 19) + "CAFE" + new string(' ', 19), centered);
        Assert.Equal(" ab  ", new ReceiptTextLayout(5).Center("ab"));
    }

    [Fact]
    public void Separator_is_columns_wide()
    {
        Assert.Equal(42, new ReceiptTextLayout(42).Separator().Length);
        Assert.Equal(32, new ReceiptTextLayout(32).Separator().Length);
    }

    [Fact]
    public void ItemRows_show_quantity_unit_price_and_amount()
    {
        var layout = new ReceiptTextLayout(42);

        var multi = Assert.Single(layout.ItemRows(2, "Iced Coffee", 25000m, 50000m));
        Assert.StartsWith("2 x Iced Coffee @ " + ReceiptTextLayout.FormatAmount(25000m), multi);
        Assert.EndsWith(ReceiptTextLayout.FormatAmount(50000m), multi);

        var single = Assert.Single(layout.ItemRows(1, "Tea", 10000m, 10000m));
        Assert.StartsWith("1 x Tea", single);
        Assert.DoesNotContain("@", single);
    }

    [Fact]
    public void FormatAmount_has_no_currency_symbol()
    {
        var text = ReceiptTextLayout.FormatAmount(1234567m);

        Assert.All(text, c => Assert.True(char.IsDigit(c) || c is ',' or '.' or ' ' or ' ', $"unexpected '{c}'"));
    }
}
