using System.Net;
using System.Text;
using Cafe.Server.Data;
using Cafe.Tests.Infrastructure;

namespace Cafe.Tests;

/// <summary>Phase 7 (REPT-01/02): every report section exports escaped, authenticated CSV.</summary>
public class ReportExportTests : ReportTestBase
{
    public static TheoryData<string, string> Headers => new()
    {
        { "summary", "Metric,Value" },
        { "days", "Date,Collected,Cash,Card" },
        { "weeks", "WeekStart,IsoWeek,Collected" },
        { "station-types", "StationType,Collected,Hours" },
        { "products", "Product,Category,Quantity,Revenue" },
        { "categories", "Category,Quantity,Revenue" },
        { "methods", "Method,Collected" },
    };

    private string Day => DateOnly.FromDateTime(Start.UtcDateTime).ToString("yyyy-MM-dd");

    private HttpClient? admin;

    private async Task<(HttpResponseMessage Response, byte[] Bytes)> ExportAsync(string path)
    {
        admin ??= await Factory.CreateAuthenticatedClientAsync();
        var response = await admin.GetAsync(path);
        return (response, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [MemberData(nameof(Headers))]
    public async Task Each_section_has_header_bom_content_type_and_filename(string section, string header)
    {
        var (response, bytes) = await ExportAsync($"/reports/export/{section}?from={Day}&to={Day}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        Assert.StartsWith(header + "\r\n", text);
        Assert.Equal($"cafe-{section}-{Day}-{Day}.csv", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
    }

    [Fact]
    public async Task Empty_range_gives_header_only_except_zero_filled_days()
    {
        var (_, products) = await ExportAsync($"/reports/export/products?from={Day}&to={Day}");
        Assert.Equal("Product,Category,Quantity,Revenue\r\n", Encoding.UTF8.GetString(products).TrimStart('﻿'));

        var (_, days) = await ExportAsync($"/reports/export/days?from={Day}&to={Day}");
        Assert.Equal($"Date,Collected,Cash,Card\r\n{Day},0,0,0\r\n", Encoding.UTF8.GetString(days).TrimStart('﻿'));
    }

    [Fact]
    public async Task Product_names_are_neutralised_and_quoted()
    {
        var formula = await ProductAsync("=HYPERLINK(\"http://x\")", 1000m);
        var comma = await ProductAsync("Cola, Large", 2000m);
        var session = await PrepaidAsync(await StationAsync("PC-01"), 60);
        var order = await OrderAsync(session.Id, null, (formula, 1), (comma, 1));
        await AddPaymentAsync(PaymentMethod.Cash, Start, (AllocationTargetType.Order, order.Id, order.Total));

        var (_, bytes) = await ExportAsync($"/reports/export/products?from={Day}&to={Day}");
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\")\"", text);
        Assert.Contains("\"Cola, Large\"", text);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("Days")]
    [InlineData("..%2Fetc%2Fpasswd")]
    public async Task Unknown_section_is_404(string section)
    {
        var (response, _) = await ExportAsync($"/reports/export/{section}?from={Day}&to={Day}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?from=2025-01-01")]
    [InlineData("?from=bad&to=2025-01-01")]
    [InlineData("?from=2025-02-10&to=2025-02-01")]
    [InlineData("?from=2020-01-01&to=2025-01-01")]
    public async Task Bad_dates_are_400(string query)
    {
        var (response, _) = await ExportAsync("/reports/export/days" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Headers))]
    public async Task Anonymous_is_redirected_to_login(string section, string _)
    {
        admin ??= await Factory.CreateAuthenticatedClientAsync(); // completes first-run setup
        var anonymous = Factory.CreateNoRedirectClient();
        var response = await anonymous.GetAsync($"/reports/export/{section}?from={Day}&to={Day}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString.Replace("http://localhost", ""));
    }
}
