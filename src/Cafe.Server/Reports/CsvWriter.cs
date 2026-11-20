using System.Globalization;
using System.Text;

namespace Cafe.Server.Reports;

/// <summary>
/// Minimal CSV writer with a spreadsheet formula-injection guard. Only text cells are neutralised (a leading
/// = + - @ tab or CR gets a single quote prefix); numbers and dates are typed, so a negative amount stays "-5".
/// </summary>
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
        _ => Text(value.ToString() ?? string.Empty),
    };

    private static string Text(string text)
    {
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
