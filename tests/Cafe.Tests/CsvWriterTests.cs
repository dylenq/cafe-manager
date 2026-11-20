using Cafe.Server.Reports;

namespace Cafe.Tests;

public class CsvWriterTests
{
    private static string One(object? cell) =>
        CsvWriter.Write(["H"], [[cell]]).Split("\r\n")[1];

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tTabbed", "'\tTabbed")]
    [InlineData("=cmd|' /C calc'!A0", "'=cmd|' /C calc'!A0")]
    public void Text_cells_starting_with_formula_characters_are_neutralised(string input, string expected)
    {
        Assert.Equal(expected, One(input));
    }

    [Fact]
    public void Carriage_return_prefixed_cell_is_neutralised_and_quoted()
    {
        Assert.Equal("\"'\rx\"", One("\rx"));
    }

    [Fact]
    public void Neutralised_cell_with_a_comma_is_also_quoted()
    {
        Assert.Equal("\"'=A1,B1\"", One("=A1,B1"));
    }

    [Fact]
    public void Numbers_and_dates_are_never_prefixed()
    {
        Assert.Equal("-5", One(-5m));
        Assert.Equal("-5", One(-5));
        Assert.Equal("12.5", One(12.50m));
        Assert.Equal("56000", One(56000m));
        Assert.Equal("0", One(0m));
        Assert.Equal("2025-02-05", One(new DateOnly(2025, 2, 5)));
    }

    [Fact]
    public void Cells_with_comma_quote_or_newline_are_quoted_and_quotes_doubled()
    {
        Assert.Equal("\"a,b\"", One("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", One("say \"hi\""));
        Assert.Equal("\"line1\nline2\"", One("line1\nline2"));
        Assert.Equal("plain", One("plain"));
    }

    [Fact]
    public void Null_is_empty_and_output_is_header_then_crlf_rows()
    {
        var csv = CsvWriter.Write(["A", "B"], [["x", null], ["y", 2]]);

        Assert.Equal("A,B\r\nx,\r\ny,2\r\n", csv);
    }

    [Fact]
    public void Invariant_formatting_ignores_the_current_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1234.5", One(1234.5m));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
