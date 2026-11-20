using System.Text;
using Cafe.Server.Data;
using Cafe.Server.Receipts;

namespace Cafe.Tests;

public class EscPosEncoderTests
{
    [Fact]
    public void Control_commands_emit_the_exact_bytes()
    {
        Assert.Equal(new byte[] { 0x1B, 0x40 }, new EscPosEncoder().Initialize().ToArray());
        Assert.Equal(new byte[] { 0x1B, 0x61, 0x00 }, new EscPosEncoder().Align(EscPosAlign.Left).ToArray());
        Assert.Equal(new byte[] { 0x1B, 0x61, 0x01 }, new EscPosEncoder().Align(EscPosAlign.Center).ToArray());
        Assert.Equal(new byte[] { 0x1B, 0x61, 0x02 }, new EscPosEncoder().Align(EscPosAlign.Right).ToArray());
        Assert.Equal(new byte[] { 0x1B, 0x45, 0x01 }, new EscPosEncoder().Bold(true).ToArray());
        Assert.Equal(new byte[] { 0x1B, 0x45, 0x00 }, new EscPosEncoder().Bold(false).ToArray());
        Assert.Equal(new byte[] { 0x1D, 0x21, 0x01 }, new EscPosEncoder().DoubleHeight(true).ToArray());
        Assert.Equal(new byte[] { 0x1D, 0x21, 0x00 }, new EscPosEncoder().DoubleHeight(false).ToArray());
        Assert.Equal(new byte[] { 0x1D, 0x56, 0x42, 0x00 }, new EscPosEncoder().Cut().ToArray());
        Assert.Equal(new byte[] { 0x0A, 0x0A }, new EscPosEncoder().LineFeed(2).ToArray());
        Assert.Equal(new byte[] { 0x48, 0x69 }, new EscPosEncoder().Text("Hi").ToArray());
    }

    [Fact]
    public void A_sequence_is_the_concatenation_of_its_groups_and_the_builder_is_fluent()
    {
        var encoder = new EscPosEncoder();
        var returned = encoder.Initialize().Align(EscPosAlign.Center).Bold(true).Text("A").Bold(false).LineFeed(1).Cut();

        Assert.Same(encoder, returned);
        Assert.Equal(
            new byte[] { 0x1B, 0x40, 0x1B, 0x61, 0x01, 0x1B, 0x45, 0x01, 0x41, 0x1B, 0x45, 0x00, 0x0A, 0x1D, 0x56, 0x42, 0x00 },
            encoder.ToArray());
    }

    [Fact]
    public void ToArray_returns_a_copy()
    {
        var encoder = new EscPosEncoder().Text("A");
        var first = encoder.ToArray();
        first[0] = 0x00;

        Assert.Equal(new byte[] { 0x41 }, encoder.ToArray());
    }

    [Fact]
    public void Text_is_encoded_in_code_page_437_with_question_mark_replacement()
    {
        Assert.Equal(new byte[] { 0x43, 0x61, 0x66, 0x82 }, new EscPosEncoder().Text("Café").ToArray());
        Assert.Equal(new byte[] { 0x3F }, new EscPosEncoder().Text("\U0001F600").ToArray());
        Assert.Equal(new byte[] { 0x3F }, new EscPosEncoder().Text("漢").ToArray());
    }

    [Fact]
    public void WR05_vietnamese_text_prints_as_readable_letters_instead_of_question_marks()
    {
        // CP437 prints à, ê and á itself; only ữ and đ need folding.
        Assert.Equal("Cà phê sua dá", EscPosEncoder.ToPrintable("Cà phê sữa đá"));
        Assert.Equal(13, new EscPosEncoder().Text("Cà phê sữa đá").ToArray().Length);
        // Characters CP437 cannot print fold to the base letter; the ones it can print (à, ê, á) stay as they are.
        Assert.Equal("e a u o a D d", EscPosEncoder.ToPrintable("ễ ạ ư ơ ả Đ đ"));
        // A decomposed (NFD) string composes first: no extra '?' per combining mark.
        Assert.Equal("Cà phê sua dá", EscPosEncoder.ToPrintable("Cà phê sữa đá".Normalize(NormalizationForm.FormD)));
        Assert.DoesNotContain('?', EscPosEncoder.ToPrintable("Phở bò tái, bánh mì, Đà Nẵng".Normalize(NormalizationForm.FormD)));
    }

    [Fact]
    public void Control_characters_and_unicode_spaces_become_a_plain_space()
    {
        Assert.Equal(new byte[] { 0x41, 0x20, 0x42 }, new EscPosEncoder().Text("A\tB").ToArray());
        Assert.Equal(new byte[] { 0x41, 0x20, 0x42 }, new EscPosEncoder().Text("A B").ToArray());
        Assert.Equal(new byte[] { 0x41, 0x20, 0x42 }, new EscPosEncoder().Text("A B").ToArray());
        // An ESC in a product name must never reach the printer as a command byte.
        Assert.DoesNotContain((byte)0x1B, new EscPosEncoder().Text("A\u001B@B").ToArray());
    }

    [Fact]
    public void A_newline_inside_text_becomes_a_line_feed_and_nothing_else_emits_0A()
    {
        Assert.Equal(new byte[] { 0x41, 0x0A, 0x42 }, new EscPosEncoder().Text("A\nB").ToArray());
        Assert.Equal(new byte[] { 0x41, 0x0A, 0x42 }, new EscPosEncoder().Text("A\r\nB").ToArray());
    }

    [Fact]
    public void ToPrintable_matches_what_prints()
    {
        Assert.Equal("Café", EscPosEncoder.ToPrintable("Café"));
        Assert.Equal("e", EscPosEncoder.ToPrintable("ễ"));
        Assert.Equal("?", EscPosEncoder.ToPrintable("\U0001F600"));
        Assert.Equal("A B", EscPosEncoder.ToPrintable("A B"));
    }

    private static ReceiptDocument Doc(decimal? change = null, int columns = 42, string cafe = "Cafe", string itemName = "Iced Coffee")
    {
        var item = new ReceiptItem(itemName, 2, 25000m, 50000m);
        var target = new ReceiptTarget(ReceiptTargetKind.Order, "Order #1", null, 50000m, 50000m, 0m, [item]);
        return ReceiptBuilder.Build(new ReceiptSource(
            123, new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc, [target],
            [new ReceiptPayment(PaymentMethod.Cash, 30000m), new ReceiptPayment(PaymentMethod.Card, 20000m)], change,
            new ReceiptHeader(cafe, ["1 Main St"], "Thank you!")));
    }

    /// <summary>Strips ESC/GS command sequences and splits the rest into printed lines.</summary>
    private static List<string> PrintedLines(byte[] bytes)
    {
        var text = new StringBuilder();
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x1B && bytes[i + 1] == 0x40)
            {
                i += 1;
            }
            else if (bytes[i] == 0x1B)
            {
                i += 2;
            }
            else if (bytes[i] == 0x1D && bytes[i + 1] == 0x56)
            {
                i += 3;
            }
            else if (bytes[i] == 0x1D)
            {
                i += 2;
            }
            else
            {
                text.Append((char)bytes[i]);
            }
        }

        return text.ToString().Split('\n').ToList();
    }

    [Fact]
    public void Renderer_output_starts_with_init_and_ends_with_feed_and_cut()
    {
        var bytes = new EscPosReceiptRenderer().Render(Doc(), 42);

        Assert.Equal(new byte[] { 0x1B, 0x40 }, bytes[..2]);
        Assert.Equal(new byte[] { 0x0A, 0x0A, 0x0A, 0x1D, 0x56, 0x42, 0x00 }, bytes[^7..]);
    }

    [Fact]
    public void Renderer_prints_number_items_payments_and_change_only_when_known()
    {
        var withChange = string.Join("\n", PrintedLines(new EscPosReceiptRenderer().Render(Doc(change: 5000m), 42)));
        var without = string.Join("\n", PrintedLines(new EscPosReceiptRenderer().Render(Doc(), 42)));

        Assert.Contains("No. 000123", withChange);
        Assert.Contains("2 x Iced Coffee @", withChange);
        Assert.Contains("Cash", withChange);
        Assert.Contains("Card", withChange);
        Assert.Contains("Change", withChange);
        Assert.DoesNotContain("Change", without);
        Assert.Contains("Cafe", withChange);
        Assert.Contains("Thank you!", withChange);
    }

    [Fact]
    public void Renderer_at_32_columns_never_exceeds_the_line_width()
    {
        var doc = Doc(change: 5000m, columns: 32, cafe: "The Very Long Cafe Name Of The Gaming Place", itemName: "Extra Large Iced Vietnamese Coffee With Condensed Milk");
        var lines = PrintedLines(new EscPosReceiptRenderer().Render(doc, 32));

        Assert.All(lines, l => Assert.True(l.Length <= 32, $"'{l}' is {l.Length} chars"));
        Assert.Contains(lines, l => l.Contains("Condensed"));
    }
}
