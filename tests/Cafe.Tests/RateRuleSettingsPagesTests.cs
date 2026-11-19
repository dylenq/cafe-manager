using System.Net;
using Cafe.Server.Data;
using Cafe.Server.Pricing;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class RateRuleSettingsPagesTests : IDisposable
{
    private readonly CafeWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/rate-rules")]
    public async Task Anonymous_request_redirects_to_login(string path)
    {
        using var auth = await _factory.CreateAuthenticatedClientAsync();
        var anonymous = _factory.CreateNoRedirectClient();

        var response = await anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString.Replace("http://localhost", ""));
    }

    [Fact]
    public async Task Rate_rules_page_shows_empty_state_then_a_seeded_rule()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();

        var empty = await client.GetAsync("/rate-rules");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Contains("No rate rules yet", await empty.Content.ReadAsStringAsync());

        using (var scope = _factory.Services.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<RateRuleService>().CreateAsync(new RateRuleInput
            {
                Name = "Happy Hour Special",
                DaysOfWeek = DaysOfWeekFlags.Monday | DaysOfWeekFlags.Tuesday | DaysOfWeekFlags.Wednesday | DaysOfWeekFlags.Thursday | DaysOfWeekFlags.Friday,
                StartTime = new TimeOnly(22, 0),
                EndTime = new TimeOnly(2, 0),
                Kind = RateRuleKind.Multiplier,
                Value = 0.5m,
            });
            Assert.True(created.Success, created.Error);
        }

        var body = await (await client.GetAsync("/rate-rules")).Content.ReadAsStringAsync();
        Assert.Contains("Happy Hour Special", body);
        Assert.Contains("Mon-Fri", body);
        Assert.Contains("overnight", body);
        Assert.DoesNotContain("No rate rules yet", body);
    }

    [Fact]
    public async Task Nav_menu_links_to_rate_rules()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        var body = await (await client.GetAsync("/rate-rules")).Content.ReadAsStringAsync();
        Assert.Contains("href=\"rate-rules\"", body);
    }
}
