using System.Globalization;
using System.Text;

namespace Cafe.Server.Reports;

public static class ReportServiceCollectionExtensions
{
    public static IServiceCollection AddReportServices(this IServiceCollection services)
    {
        services.AddScoped<ReportService>();
        return services;
    }

    public static WebApplication MapReportEndpoints(this WebApplication app)
    {
        app.MapGet("/reports/export/{section}", async (string section, string? from, string? to, ReportService reports, CancellationToken ct) =>
        {
            if (!TryParseDate(from, out var fromDate) || !TryParseDate(to, out var toDate))
            {
                return Results.BadRequest("from and to must be dates in yyyy-MM-dd format.");
            }

            if (fromDate > toDate)
            {
                return Results.BadRequest("From date must not be after To date.");
            }

            var range = new ReportRange(fromDate, toDate);
            if (section != "days")
            {
                return Results.NotFound();
            }

            var data = await reports.GetAsync(range, ct);
            var csv = CsvWriter.Write(
                ["Date", "Collected", "Cash", "Card"],
                data.Days.Select(d => (IReadOnlyList<object?>)[d.Date, d.Collected, d.Cash, d.Card]));
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", $"cafe-days-{fromDate:yyyy-MM-dd}-{toDate:yyyy-MM-dd}.csv");
        }).RequireAuthorization();
        return app;
    }

    private static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
