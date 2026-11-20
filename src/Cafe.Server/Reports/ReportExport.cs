namespace Cafe.Server.Reports;

/// <summary>
/// Fixed whitelist of exportable report sections. The section name from the URL is only ever used as a dictionary
/// key here, never to build paths or queries.
/// </summary>
public static class ReportExport
{
    public sealed record Section(IReadOnlyList<string> Header, Func<ReportData, IEnumerable<IReadOnlyList<object?>>> Rows);

    private static readonly Dictionary<string, Section> Sections = new(StringComparer.Ordinal)
    {
        ["summary"] = new(["Metric", "Value"], d =>
        [
            ["Total collected", d.Summary.Collected],
            ["Cash", d.Summary.Cash],
            ["Card", d.Summary.Card],
            ["Sessions", d.Summary.SessionCount],
            ["Session hours", d.Summary.SessionHours],
            ["Average session minutes", d.Summary.AverageSessionMinutes],
            ["Charged unpaid", d.Summary.ChargedUnpaid],
        ]),
        ["days"] = new(["Date", "Collected", "Cash", "Card"],
            d => d.Days.Select(r => (IReadOnlyList<object?>)[r.Date, r.Collected, r.Cash, r.Card])),
        ["weeks"] = new(["WeekStart", "IsoWeek", "Collected"],
            d => d.Weeks.Select(r => (IReadOnlyList<object?>)[r.WeekStart, r.IsoLabel, r.Collected])),
        ["station-types"] = new(["StationType", "Collected", "Hours"],
            d => d.StationTypes.Select(r => (IReadOnlyList<object?>)[r.Name, r.Collected, r.Hours])),
        ["products"] = new(["Product", "Category", "Quantity", "Revenue"],
            d => d.Products.Select(r => (IReadOnlyList<object?>)[r.Name, r.Category, r.Quantity, r.Revenue])),
        ["categories"] = new(["Category", "Quantity", "Revenue"],
            d => d.Categories.Select(r => (IReadOnlyList<object?>)[r.Category, r.Quantity, r.Revenue])),
        ["methods"] = new(["Method", "Collected"],
            d => d.Methods.Select(r => (IReadOnlyList<object?>)[r.Method.ToString(), r.Collected])),
    };

    public static IReadOnlyCollection<string> Names => Sections.Keys;

    public static bool TryGet(string section, out Section value) => Sections.TryGetValue(section, out value!);
}
