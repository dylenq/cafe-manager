using System.Net;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class DashboardPageTests : IDisposable
{
    private readonly CafeWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Anonymous_request_redirects_to_login()
    {
        using var auth = await _factory.CreateAuthenticatedClientAsync();
        var anonymous = _factory.CreateNoRedirectClient();

        var response = await anonymous.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString.Replace("http://localhost", ""));
    }

    [Fact]
    public async Task Authenticated_empty_install_shows_empty_state()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("No stations yet", body);
        Assert.Contains("/station-types", body);
        Assert.Contains("/stations", body);
    }

    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<StationTypeService>();
        var stations = scope.ServiceProvider.GetRequiredService<StationService>();
        var typeId = (await types.CreateAsync(new StationTypeInput { Name = "Premium Rig", HourlyRate = 5m })).Value!.Id;
        Assert.True((await stations.CreateAsync(new StationInput
        {
            Name = "PS5-1", StationTypeId = typeId, Kind = StationKind.Console, SortOrder = 2,
        })).Success);
        Assert.True((await stations.CreateAsync(new StationInput
        {
            Name = "PC-01", StationTypeId = typeId, Kind = StationKind.PC, AgentMachineId = "pc-01", SortOrder = 1,
        })).Success);
        Assert.True((await stations.CreateAsync(new StationInput
        {
            Name = "Hidden", StationTypeId = typeId, Kind = StationKind.PC, SortOrder = 0, IsEnabled = false,
        })).Success);
    }

    [Fact]
    public async Task Cards_show_enabled_stations_with_type_kind_and_status()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        await SeedAsync();

        var body = await client.GetStringAsync("/");

        Assert.Contains("PC-01", body);
        Assert.Contains("PS5-1", body);
        Assert.Contains("Premium Rig", body);
        Assert.DoesNotContain("Hidden", body);

        var pcCard = Card(body, "PC-01");
        Assert.Contains("Offline", pcCard);
        Assert.Contains(">PC<", pcCard);
        var consoleCard = Card(body, "PS5-1");
        Assert.Contains("Idle", consoleCard);
        Assert.Contains(">Console<", consoleCard);
    }

    [Fact]
    public async Task Cards_render_in_sort_order_then_name()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        await SeedAsync();

        var body = await client.GetStringAsync("/");

        Assert.True(body.IndexOf("PC-01", StringComparison.Ordinal) < body.IndexOf("PS5-1", StringComparison.Ordinal));
    }

    /// <summary>The slice of the page from the station's name to the next card (or end).</summary>
    private static string Card(string html, string name)
    {
        var start = html.IndexOf($">{name}<", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} card not found");
        var next = html.IndexOf("station-card", start, StringComparison.Ordinal);
        return next < 0 ? html[start..] : html[start..next];
    }
}
