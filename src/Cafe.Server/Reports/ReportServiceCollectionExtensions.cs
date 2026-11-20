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
            if (!ReportExport.TryGet(section, out var definition))
            {
                return Results.NotFound();
            }

            if (!TryParseDate(from, out var fromDate) || !TryParseDate(to, out var toDate))
            {
                return Results.BadRequest("from and to must be dates in yyyy-MM-dd format.");
            }

            if (!ReportRange.TryResolve(ReportPreset.Custom, fromDate, fromDate, toDate, out var range, out var rangeError))
            {
                return Results.BadRequest(rangeError);
            }

            var data = await reports.GetAsync(range!, ct);
            var csv = CsvWriter.Write(definition.Header, definition.Rows(data));
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", $"cafe-{section}-{fromDate:yyyy-MM-dd}-{toDate:yyyy-MM-dd}.csv");
        }).RequireAuthorization();
        return app;
    }

    private static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
