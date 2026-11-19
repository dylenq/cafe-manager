using System.Net;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class StationPagesTests : IDisposable
{
    private readonly CafeWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/stations")]
    [InlineData("/station-types")]
    public async Task Anonymous_request_redirects_to_login(string path)
    {
        // Configure the install first so the setup gate does not intercept the request.
        using var auth = await _factory.CreateAuthenticatedClientAsync();
        var anonymous = _factory.CreateNoRedirectClient();

        var response = await anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString.Replace("http://localhost", ""));
    }

    [Theory]
    [InlineData("/stations")]
    [InlineData("/station-types")]
    public async Task Authenticated_empty_install_renders_empty_state(string path)
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(path == "/stations" ? "No stations yet" : "No station types yet", body);
    }

    [Fact]
    public async Task Pages_render_persisted_data()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var types = scope.ServiceProvider.GetRequiredService<StationTypeService>();
            var stations = scope.ServiceProvider.GetRequiredService<StationService>();
            var typeId = (await types.CreateAsync(new StationTypeInput { Name = "Premium Rig", HourlyRate = 7500m })).Value!.Id;
            await stations.CreateAsync(new StationInput
            {
                Name = "Booth-7", StationTypeId = typeId, Kind = StationKind.Console, TvIpAddress = "192.168.1.50"
            });
        }

        var stationsBody = await (await client.GetAsync("/stations")).Content.ReadAsStringAsync();
        Assert.Contains("Booth-7", stationsBody);
        Assert.Contains("Premium Rig", stationsBody);
        Assert.Contains("Console", stationsBody);
        Assert.Contains("192.168.1.50", stationsBody);

        var typesBody = await (await client.GetAsync("/station-types")).Content.ReadAsStringAsync();
        Assert.Contains("Premium Rig", typesBody);
        Assert.Contains("7,500.00", typesBody);
    }

    [Fact]
    public async Task Stations_page_without_types_points_staff_to_station_types()
    {
        using var client = await _factory.CreateAuthenticatedClientAsync();
        var body = await (await client.GetAsync("/stations")).Content.ReadAsStringAsync();
        // The form is closed until "Add station" is clicked, so the hint lives in the empty-state guidance too.
        Assert.Contains("station types", body);
        Assert.Contains("href=\"/station-types\"", body);
    }
}
