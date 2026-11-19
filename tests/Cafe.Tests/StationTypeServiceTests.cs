using Cafe.Server.Data;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class StationTypeServiceTests : IDisposable
{
    private readonly CafeWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private (IServiceScope Scope, StationTypeService Types, StationService Stations) Services()
    {
        var scope = _factory.Services.CreateScope();
        return (scope, scope.ServiceProvider.GetRequiredService<StationTypeService>(),
            scope.ServiceProvider.GetRequiredService<StationService>());
    }

    private static StationTypeInput Input(string name, decimal rate = 1m, int sort = 0) =>
        new() { Name = name, HourlyRate = rate, SortOrder = sort };

    [Fact]
    public async Task Create_succeeds_and_persists_exact_decimal()
    {
        var (scope, types, _) = Services();
        using var _s = scope;
        var result = await types.CreateAsync(Input("PC", 5000.25m));
        Assert.True(result.Success);
        var loaded = await types.GetAsync(result.Value!.Id);
        Assert.Equal(5000.25m, loaded!.HourlyRate);
    }

    [Fact]
    public async Task Create_zero_rate_is_allowed()
    {
        var (scope, types, _) = Services();
        using var _s = scope;
        Assert.True((await types.CreateAsync(Input("Free", 0m))).Success);
    }

    [Theory]
    [InlineData("pc")]
    [InlineData(" PC ")]
    [InlineData("Pc")]
    public async Task Create_duplicate_name_ignoring_case_and_whitespace_fails(string dup)
    {
        var (scope, types, _) = Services();
        using var _s = scope;
        Assert.True((await types.CreateAsync(Input("PC"))).Success);
        var result = await types.CreateAsync(Input(dup));
        Assert.False(result.Success);
        Assert.Contains("already exists", result.Error);
        Assert.Single(await types.ListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_blank_name_fails_with_field_error(string name)
    {
        var (scope, types, _) = Services();
        using var _s = scope;
        var result = await types.CreateAsync(Input(name));
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("Name"));
    }

    [Fact]
    public async Task Create_negative_rate_fails_with_field_error()
    {
        var (scope, types, _) = Services();
        using var _s = scope;
        var result = await types.CreateAsync(Input("X", -0.01m));
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("HourlyRate"));
    }

    [Fact]
    public async Task Update_same_name_on_same_row_succeeds()
    {
        var (scope, types, _) = Services();
        using var _s = scope;
        var id = (await types.CreateAsync(Input("PC", 1m))).Value!.Id;
        var result = await types.UpdateAsync(id, Input("PC", 2m, 3));
        Assert.True(result.Success);
        var loaded = await types.GetAsync(id);
        Assert.Equal(2m, loaded!.HourlyRate);
        Assert.Equal(3, loaded.SortOrder);
    }

    [Fact]
    public async Task Update_to_another_rows_name_fails()
    {
        var (scope, types, _) = Services();
        using var _s = scope;
        await types.CreateAsync(Input("PC"));
        var id = (await types.CreateAsync(Input("Console"))).Value!.Id;
        var result = await types.UpdateAsync(id, Input("pc"));
        Assert.False(result.Success);
        Assert.Contains("already exists", result.Error);
        Assert.Equal("Console", (await types.GetAsync(id))!.Name);
    }

    [Fact]
    public async Task Update_missing_id_reports_no_longer_exists()
    {
        var (scope, types, _) = Services();
        using var _s = scope;
        var result = await types.UpdateAsync(999, Input("X"));
        Assert.False(result.Success);
        Assert.Equal(FailureKind.NotFound, result.Kind);
        Assert.Contains("no longer exists", result.Error);
    }

    [Fact]
    public async Task Delete_missing_id_reports_no_longer_exists()
    {
        var (scope, types, _) = Services();
        using var _s = scope;
        var result = await types.DeleteAsync(999);
        Assert.False(result.Success);
        Assert.Contains("no longer exists", result.Error);
    }

    [Fact]
    public async Task Delete_empty_type_succeeds()
    {
        var (scope, types, _) = Services();
        using var _s = scope;
        var id = (await types.CreateAsync(Input("PC"))).Value!.Id;
        Assert.True((await types.DeleteAsync(id)).Success);
        Assert.Null(await types.GetAsync(id));
    }

    [Fact]
    public async Task Delete_type_with_stations_is_blocked_and_nothing_is_removed()
    {
        var (scope, types, stations) = Services();
        using var _s = scope;
        var typeId = (await types.CreateAsync(Input("Gaming PC"))).Value!.Id;
        await stations.CreateAsync(new StationInput { Name = "PC-01", StationTypeId = typeId, Kind = StationKind.PC });
        await stations.CreateAsync(new StationInput { Name = "PC-02", StationTypeId = typeId, Kind = StationKind.PC });

        var result = await types.DeleteAsync(typeId);

        Assert.False(result.Success);
        Assert.Contains("Gaming PC", result.Error);
        Assert.Contains("2", result.Error);
        Assert.NotNull(await types.GetAsync(typeId));
        Assert.Equal(2, (await stations.ListAsync()).Count);
    }

    [Fact]
    public async Task List_orders_by_sort_order_then_name_ignoring_case()
    {
        var (scope, types, _) = Services();
        using var _s = scope;
        await types.CreateAsync(Input("zeta", 1m, 0));
        await types.CreateAsync(Input("Alpha", 1m, 1));
        await types.CreateAsync(Input("beta", 1m, 0));
        await types.CreateAsync(Input("Apple", 1m, 0));
        var names = (await types.ListAsync()).Select(t => t.Name).ToArray();
        Assert.Equal(["Apple", "beta", "zeta", "Alpha"], names);
    }
}
