using System.Globalization;
using System.Text;

namespace Cafe.Server.Reports;

/// <summary>Minimal CSV writer. Escape rules are completed in the next task.</summary>
public static class CsvWriter
{
    public static string Write(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', header.Select(h => Cell(h)))).Append("\r\n");
        foreach (var row in rows)
        {
            sb.Append(string.Join(',', row.Select(Cell))).Append("\r\n");
        }

        return sb.ToString();
    }

    private static string Cell(object? value) => value switch
    {
        null => string.Empty,
        decimal d => d.ToString("0.##", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => Quote(value.ToString() ?? string.Empty),
    };

    private static string Quote(string text) =>
        text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
}
