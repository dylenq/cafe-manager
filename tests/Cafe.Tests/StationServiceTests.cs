using Cafe.Server.Data;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class StationServiceTests : IDisposable
{
    private readonly CafeWebApplicationFactory _factory = new();
    private readonly IServiceScope _scope;
    private readonly StationTypeService _types;
    private readonly StationService _stations;

    public StationServiceTests()
    {
        _scope = _factory.Services.CreateScope();
        _types = _scope.ServiceProvider.GetRequiredService<StationTypeService>();
        _stations = _scope.ServiceProvider.GetRequiredService<StationService>();
    }

    public void Dispose()
    {
        _scope.Dispose();
        _factory.Dispose();
    }

    private async Task<int> TypeAsync(string name = "PC") =>
        (await _types.CreateAsync(new StationTypeInput { Name = name, HourlyRate = 5m })).Value!.Id;

    private static StationInput Pc(int typeId, string name = "PC-01", string? machine = "pc-01") =>
        new() { Name = name, StationTypeId = typeId, Kind = StationKind.PC, AgentMachineId = machine };

    private static StationInput Console(int typeId, string name = "PS5-1", string? ip = "192.168.1.50", string? mac = "aa-bb-cc-dd-ee-ff") =>
        new() { Name = name, StationTypeId = typeId, Kind = StationKind.Console, TvIpAddress = ip, TvMacAddress = mac };

    [Fact]
    public async Task Create_pc_station_with_machine_id_succeeds()
    {
        var typeId = await TypeAsync();
        var result = await _stations.CreateAsync(Pc(typeId));
        Assert.True(result.Success);
        var loaded = await _stations.GetAsync(result.Value!.Id);
        Assert.Equal("pc-01", loaded!.AgentMachineId);
        Assert.Equal("PC", loaded.StationType.Name);
        Assert.True(loaded.IsEnabled);
    }

    [Fact]
    public async Task Create_console_station_normalises_mac()
    {
        var typeId = await TypeAsync("Console");
        var result = await _stations.CreateAsync(Console(typeId));
        Assert.True(result.Success);
        var loaded = await _stations.GetAsync(result.Value!.Id);
        Assert.Equal("AA:BB:CC:DD:EE:FF", loaded!.TvMacAddress);
        Assert.Equal("192.168.1.50", loaded.TvIpAddress);
    }

    [Theory]
    [InlineData("256.1.1.1")]
    [InlineData("1.2.3")]
    [InlineData("abc")]
    [InlineData("1.2.3.4.5")]
    public async Task Create_rejects_invalid_ipv4(string ip)
    {
        var typeId = await TypeAsync();
        var result = await _stations.CreateAsync(Console(typeId, ip: ip));
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("TvIpAddress"));
        Assert.Empty(await _stations.ListAsync());
    }

    [Theory]
    [InlineData("zz:zz")]
    [InlineData("AA:BB:CC:DD:EE")]
    public async Task Create_rejects_invalid_mac(string mac)
    {
        var typeId = await TypeAsync();
        var result = await _stations.CreateAsync(Console(typeId, mac: mac));
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("TvMacAddress"));
    }

    [Fact]
    public async Task Whitespace_only_optional_fields_are_stored_as_null()
    {
        var typeId = await TypeAsync();
        var id = (await _stations.CreateAsync(Console(typeId, ip: "  ", mac: "   "))).Value!.Id;
        var console = await _stations.GetAsync(id);
        Assert.Null(console!.TvIpAddress);
        Assert.Null(console.TvMacAddress);

        var pcId = (await _stations.CreateAsync(Pc(typeId, "PC-9", "   "))).Value!.Id;
        Assert.Null((await _stations.GetAsync(pcId))!.AgentMachineId);
    }

    [Fact]
    public async Task Pc_kind_drops_tv_fields()
    {
        var typeId = await TypeAsync();
        var input = Pc(typeId);
        input.TvIpAddress = "10.0.0.1";
        input.TvMacAddress = "AA:BB:CC:DD:EE:FF";
        var id = (await _stations.CreateAsync(input)).Value!.Id;
        var loaded = await _stations.GetAsync(id);
        Assert.Null(loaded!.TvIpAddress);
        Assert.Null(loaded.TvMacAddress);
    }

    [Fact]
    public async Task Console_kind_drops_machine_id()
    {
        var typeId = await TypeAsync();
        var input = Console(typeId);
        input.AgentMachineId = "leftover";
        var id = (await _stations.CreateAsync(input)).Value!.Id;
        Assert.Null((await _stations.GetAsync(id))!.AgentMachineId);
    }

    [Fact]
    public async Task Update_switching_kind_clears_other_fields_and_keeps_tv_token()
    {
        var typeId = await TypeAsync();
        var id = (await _stations.CreateAsync(Console(typeId))).Value!.Id;
        var dbFactory = _scope.ServiceProvider.GetRequiredService<IDbContextFactory<CafeDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Stations.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.TvToken, "secret-token"));
        }

        var toPc = await _stations.UpdateAsync(id, Pc(typeId, "PS5-1", "machine-x"));
        Assert.True(toPc.Success);
        var pc = await _stations.GetAsync(id);
        Assert.Equal("machine-x", pc!.AgentMachineId);
        Assert.Null(pc.TvIpAddress);
        Assert.Null(pc.TvMacAddress);
        Assert.Equal("secret-token", pc.TvToken);

        Assert.True((await _stations.UpdateAsync(id, Console(typeId))).Success);
        var console = await _stations.GetAsync(id);
        Assert.Null(console!.AgentMachineId);
        Assert.Equal("secret-token", console.TvToken);
    }

    [Fact]
    public async Task Create_unknown_station_type_fails()
    {
        var result = await _stations.CreateAsync(Pc(12345));
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("StationTypeId"));
    }

    [Fact]
    public async Task Create_without_choosing_a_type_fails_validation()
    {
        var result = await _stations.CreateAsync(Pc(0));
        Assert.False(result.Success);
        Assert.Equal("Choose a station type.", result.FieldErrors["StationTypeId"]);
    }

    [Fact]
    public async Task Create_duplicate_name_ignoring_case_fails()
    {
        var typeId = await TypeAsync();
        await _stations.CreateAsync(Pc(typeId, "PC-01", "a"));
        var result = await _stations.CreateAsync(Pc(typeId, " pc-01 ", "b"));
        Assert.False(result.Success);
        Assert.Contains("already exists", result.Error);
    }

    [Fact]
    public async Task Duplicate_machine_id_ignoring_case_fails_but_own_id_can_be_kept()
    {
        var typeId = await TypeAsync();
        var first = (await _stations.CreateAsync(Pc(typeId, "PC-01", "Machine-A"))).Value!.Id;
        var dup = await _stations.CreateAsync(Pc(typeId, "PC-02", "machine-a"));
        Assert.False(dup.Success);
        Assert.True(dup.FieldErrors.ContainsKey("AgentMachineId"));

        Assert.True((await _stations.UpdateAsync(first, Pc(typeId, "PC-01b", "Machine-A"))).Success);
    }

    [Fact]
    public async Task Update_to_another_stations_name_fails()
    {
        var typeId = await TypeAsync();
        await _stations.CreateAsync(Pc(typeId, "PC-01", "a"));
        var id = (await _stations.CreateAsync(Pc(typeId, "PC-02", "b"))).Value!.Id;
        var result = await _stations.UpdateAsync(id, Pc(typeId, "pc-01", "b"));
        Assert.False(result.Success);
        Assert.Equal("PC-02", (await _stations.GetAsync(id))!.Name);
    }

    [Fact]
    public async Task Blank_name_fails_with_field_error()
    {
        var typeId = await TypeAsync();
        var result = await _stations.CreateAsync(Pc(typeId, "   "));
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("Name"));
    }

    [Fact]
    public async Task Update_and_delete_missing_station_report_no_longer_exists()
    {
        var typeId = await TypeAsync();
        var update = await _stations.UpdateAsync(999, Pc(typeId));
        Assert.False(update.Success);
        Assert.Contains("no longer exists", update.Error);
        Assert.Equal(FailureKind.NotFound, update.Kind);

        var delete = await _stations.DeleteAsync(999);
        Assert.False(delete.Success);
        Assert.Contains("no longer exists", delete.Error);
        Assert.Equal(FailureKind.NotFound, delete.Kind);
    }

    [Fact]
    public async Task Pc_station_ignores_invalid_leftover_tv_fields()
    {
        var typeId = await TypeAsync();
        var input = Pc(typeId);
        input.TvIpAddress = "999.1.1.1";
        input.TvMacAddress = "not-a-mac";

        var result = await _stations.CreateAsync(input);

        Assert.True(result.Success);
        var loaded = await _stations.GetAsync(result.Value!.Id);
        Assert.Null(loaded!.TvIpAddress);
        Assert.Null(loaded.TvMacAddress);
    }

    [Fact]
    public async Task Console_station_ignores_invalid_leftover_machine_id_but_still_validates_tv_fields()
    {
        var typeId = await TypeAsync("Console");
        var ok = Console(typeId);
        ok.AgentMachineId = new string('x', 101);
        Assert.True((await _stations.CreateAsync(ok)).Success);

        var bad = Console(typeId, "PS5-2", ip: "999.1.1.1");
        var result = await _stations.CreateAsync(bad);
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("TvIpAddress"));
    }

    [Fact]
    public async Task Deleted_station_type_is_a_validation_failure_not_a_missing_station()
    {
        var typeId = await TypeAsync();
        var otherTypeId = await TypeAsync("Other");
        var id = (await _stations.CreateAsync(Pc(typeId))).Value!.Id;

        var result = await _stations.UpdateAsync(id, Pc(otherTypeId + 100));

        Assert.False(result.Success);
        Assert.Equal(FailureKind.Validation, result.Kind);
        Assert.True(result.FieldErrors.ContainsKey("StationTypeId"));
    }

    [Fact]
    public async Task Delete_removes_station_but_keeps_type()
    {
        var typeId = await TypeAsync();
        var id = (await _stations.CreateAsync(Pc(typeId))).Value!.Id;
        Assert.True((await _stations.DeleteAsync(id)).Success);
        Assert.Null(await _stations.GetAsync(id));
        Assert.NotNull(await _types.GetAsync(typeId));
    }

    [Fact]
    public async Task List_orders_by_sort_order_then_name_ignoring_case_and_includes_type()
    {
        var typeId = await TypeAsync();
        StationInput S(string n, int sort) { var i = Pc(typeId, n, null); i.SortOrder = sort; return i; }
        await _stations.CreateAsync(S("zed", 0));
        await _stations.CreateAsync(S("Alpha", 2));
        await _stations.CreateAsync(S("beta", 0));
        var list = await _stations.ListAsync();
        Assert.Equal(["beta", "zed", "Alpha"], list.Select(s => s.Name).ToArray());
        Assert.All(list, s => Assert.Equal("PC", s.StationType.Name));
    }

    [Fact]
    public async Task Disabled_flag_round_trips()
    {
        var typeId = await TypeAsync();
        var input = Pc(typeId);
        input.IsEnabled = false;
        var id = (await _stations.CreateAsync(input)).Value!.Id;
        Assert.False((await _stations.GetAsync(id))!.IsEnabled);
    }
}
