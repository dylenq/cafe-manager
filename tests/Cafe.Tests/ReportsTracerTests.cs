using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Cafe.Server.Data;
using Cafe.Server.Payments;
using Cafe.Server.Reports;
using Cafe.Server.Sessions;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>Phase 7 tracer (REPT-01): real checkout -> ReportService -> /reports page -> CSV endpoint.</summary>
public class ReportsTracerTests : ReportTestBase
{
    private async Task CheckoutAsync()
    {
        var stationId = await StationAsync("PC-01");
        var productId = await ProductAsync("Iced Coffee", 25000m);
        var session = await PrepaidAsync(stationId, 60);
        await OrderAsync(session.Id, null, (productId, 2));
        var quote = (await Svc(sp => sp.GetRequiredService<SessionService>().QuoteStopAsync(session.Id))).Value!;
        var result = await Svc(sp => sp.GetRequiredService<CheckoutService>()
            .StopAndSettleAsync(session.Id, quote.Version, quote.Amount, [new TenderLine(PaymentMethod.Cash, 56000m)], 56000m));
        Assert.True(result.Success, result.Error);
        Assert.Null(result.Value!.PaymentError);
    }

    private string LocalDay => DateOnly.FromDateTime(Start.UtcDateTime).ToString("yyyy-MM-dd");

    [Fact]
    public async Task REPT01_checkout_shows_in_service_page_and_csv()
    {
        await CheckoutAsync();
        var day = DateOnly.FromDateTime(Start.UtcDateTime);

        var data = await Svc(sp => sp.GetRequiredService<ReportService>().GetAsync(new ReportRange(day, day)));

        Assert.Equal(56000m, data.Summary.Collected);
        Assert.Equal(56000m, data.Summary.Cash);
        Assert.Equal(0m, data.Summary.Card);
        var row = Assert.Single(data.Days);
        Assert.Equal(day, row.Date);
        Assert.Equal(56000m, row.Collected);

        using var admin = await Factory.CreateAuthenticatedClientAsync();
        var page = await admin.GetAsync("/reports");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Assert.Contains(LocalDay, html);
        Assert.Contains(56000m.ToString("C"), html);

        var csv = await admin.GetAsync($"/reports/export/days?from={LocalDay}&to={LocalDay}");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        var lines = Encoding.UTF8.GetString(bytes).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Date,Collected,Cash,Card", lines[0]);
        Assert.Equal($"{LocalDay},56000,56000,0", Assert.Single(lines.Skip(1)));
    }

    [Fact]
    public async Task REPT01_export_requires_login_and_rejects_bad_dates()
    {
        using var admin = await Factory.CreateAuthenticatedClientAsync();

        var bad = await admin.GetAsync("/reports/export/days?from=nope&to=2025-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var anonymous = Factory.CreateNoRedirectClient();
        var denied = await anonymous.GetAsync($"/reports/export/days?from={LocalDay}&to={LocalDay}");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.StartsWith("/login", denied.Headers.Location!.OriginalString.Replace("http://localhost", ""));
    }
}
