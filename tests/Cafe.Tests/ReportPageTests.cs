using System.Net;
using Cafe.Server.Data;
using Cafe.Tests.Infrastructure;

namespace Cafe.Tests;

/// <summary>Phase 7 (REPT-01/02): the /reports page renders every section, the picker, export links and escapes names.</summary>
public class ReportPageTests : ReportTestBase
{
    private string Day => DateOnly.FromDateTime(Start.UtcDateTime).ToString("yyyy-MM-dd");

    private async Task<string> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private async Task SeedSaleAsync(string productName)
    {
        var product = await ProductAsync(productName, 25000m);
        var session = await PrepaidAsync(await StationAsync("PC-01"), 60);
        var order = await OrderAsync(session.Id, null, (product, 2));
        await AddPaymentAsync(PaymentMethod.Cash, Start, (AllocationTargetType.Order, order.Id, order.Total));
    }

    [Fact]
    public async Task Page_shows_presets_sections_export_links_and_chart()
    {
        await SeedSaleAsync("Iced Coffee");
        using var admin = await Factory.CreateAuthenticatedClientAsync();

        var html = await GetAsync(admin, $"/reports?preset=Custom&from={Day}&to={Day}");

        foreach (var preset in new[] { "Today", "Yesterday", "This week", "Last 7 days", "This month", "Last month", "Custom" })
        {
            Assert.Contains(preset, html);
        }

        foreach (var heading in new[]
        {
            "Summary", "Revenue by day", "Revenue by week", "By station type", "By product", "By category", "By payment method",
            "Charged (unpaid)", "Owed but not yet collected",
        })
        {
            Assert.Contains(heading, html);
        }

        Assert.Equal(7, html.Split("Export CSV").Length - 1);
        foreach (var section in new[] { "summary", "days", "weeks", "station-types", "products", "categories", "methods" })
        {
            Assert.Contains($"/reports/export/{section}?from={Day}&to={Day}", html);
        }

        Assert.Contains("<svg", html);
        Assert.Contains("<text", html);
        Assert.DoesNotContain("<!text", html);
        Assert.Contains("Iced Coffee", html);
        Assert.DoesNotContain("cdn", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inverted_custom_range_shows_the_validation_message()
    {
        using var admin = await Factory.CreateAuthenticatedClientAsync();

        var html = await GetAsync(admin, "/reports?preset=Custom&from=2025-02-10&to=2025-02-01");

        Assert.Contains("From date must not be after To date.", html);
        Assert.DoesNotContain("Export CSV", html);
    }

    [Fact]
    public async Task Product_names_are_html_encoded()
    {
        await SeedSaleAsync("<script>alert(1)</script>");
        using var admin = await Factory.CreateAuthenticatedClientAsync();

        var response = await admin.GetAsync($"/reports?preset=Custom&from={Day}&to={Day}");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", raw);
        Assert.DoesNotContain("<script>alert", raw);
    }

    [Fact]
    public async Task Anonymous_is_redirected_to_login()
    {
        using var admin = await Factory.CreateAuthenticatedClientAsync(); // completes first-run setup
        var anonymous = Factory.CreateNoRedirectClient();

        var response = await anonymous.GetAsync("/reports");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString.Replace("http://localhost", ""));
    }

    [Fact]
    public async Task Empty_install_shows_zero_tiles_and_empty_messages()
    {
        using var admin = await Factory.CreateAuthenticatedClientAsync();

        var html = await GetAsync(admin, "/reports");

        Assert.Contains("No revenue in this range.", html);
        Assert.Contains(0m.ToString("C"), html);
        Assert.DoesNotContain("<svg class=\"revenue-chart", html);
    }
}
