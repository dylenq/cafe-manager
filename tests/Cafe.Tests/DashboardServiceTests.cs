using Cafe.Server.Dashboard;
using Cafe.Server.Data;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class DashboardServiceTests : IDisposable
{
    private readonly CafeWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private sealed class PausedProvider : IStationStatusProvider
    {
        public StationStatus GetStatus(Station station) => StationStatus.Paused;
    }

    private static async Task<int> TypeAsync(IServiceScope scope, string name = "Standard") =>
        (await scope.ServiceProvider.GetRequiredService<StationTypeService>()
            .CreateAsync(new StationTypeInput { Name = name, HourlyRate = 5m })).Value!.Id;

    private static async Task AddAsync(IServiceScope scope, StationInput input) =>
        Assert.True((await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(input)).Success);

    [Theory]
    [InlineData("pc-01", null, StationStatus.Offline)]
    [InlineData(null, "192.168.1.50", StationStatus.Offline)]
    [InlineData(null, null, StationStatus.Idle)]
    [InlineData("   ", "", StationStatus.Idle)]
    public void Default_provider_applies_phase_one_rule(string? machine, string? ip, StationStatus expected)
    {
        var station = new Station { AgentMachineId = machine, TvIpAddress = ip };
        Assert.Equal(expected, new DefaultStationStatusProvider().GetStatus(station));
    }

    [Fact]
    public async Task Empty_database_gives_no_cards()
    {
        using var scope = _factory.Services.CreateScope();
        var cards = await scope.ServiceProvider.GetRequiredService<DashboardService>().GetCardsAsync();
        Assert.Empty(cards);
    }

    [Fact]
    public async Task Disabled_stations_are_excluded()
    {
        using var scope = _factory.Services.CreateScope();
        var typeId = await TypeAsync(scope);
        await AddAsync(scope, new StationInput { Name = "Shown", StationTypeId = typeId, Kind = StationKind.PC, IsEnabled = true });
        await AddAsync(scope, new StationInput { Name = "Hidden", StationTypeId = typeId, Kind = StationKind.PC, IsEnabled = false });

        var cards = await scope.ServiceProvider.GetRequiredService<DashboardService>().GetCardsAsync();

        Assert.Equal(["Shown"], cards.Select(c => c.Name));
    }

    [Fact]
    public async Task Cards_are_ordered_by_sort_order_then_name_case_insensitively()
    {
        using var scope = _factory.Services.CreateScope();
        var typeId = await TypeAsync(scope);
        await AddAsync(scope, new StationInput { Name = "b", StationTypeId = typeId, Kind = StationKind.PC, SortOrder = 1 });
        await AddAsync(scope, new StationInput { Name = "A", StationTypeId = typeId, Kind = StationKind.PC, SortOrder = 1 });
        await AddAsync(scope, new StationInput { Name = "z", StationTypeId = typeId, Kind = StationKind.PC, SortOrder = 0 });

        var cards = await scope.ServiceProvider.GetRequiredService<DashboardService>().GetCardsAsync();

        Assert.Equal(["z", "A", "b"], cards.Select(c => c.Name));
    }

    [Fact]
    public async Task Cards_carry_name_type_kind_and_status()
    {
        using var scope = _factory.Services.CreateScope();
        var typeId = await TypeAsync(scope, "Premium Rig");
        await AddAsync(scope, new StationInput
        {
            Name = "PC-01", StationTypeId = typeId, Kind = StationKind.PC, AgentMachineId = "pc-01",
        });
        await AddAsync(scope, new StationInput { Name = "PS5-1", StationTypeId = typeId, Kind = StationKind.Console, SortOrder = 1 });

        var cards = await scope.ServiceProvider.GetRequiredService<DashboardService>().GetCardsAsync();

        var pc = cards[0];
        Assert.Equal(("PC-01", "Premium Rig", StationKind.PC, StationStatus.Offline), (pc.Name, pc.TypeName, pc.Kind, pc.Status));
        var console = cards[1];
        Assert.Equal(("PS5-1", StationKind.Console, StationStatus.Idle), (console.Name, console.Kind, console.Status));
    }

    [Fact]
    public async Task Swapping_the_provider_registration_changes_card_status()
    {
        using var swapped = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IStationStatusProvider, PausedProvider>()));
        using var scope = swapped.Services.CreateScope();
        var typeId = await TypeAsync(scope);
        await AddAsync(scope, new StationInput { Name = "PC-01", StationTypeId = typeId, Kind = StationKind.PC, AgentMachineId = "pc-01" });

        var cards = await scope.ServiceProvider.GetRequiredService<DashboardService>().GetCardsAsync();

        Assert.Equal(StationStatus.Paused, Assert.Single(cards).Status);
    }
}
