using Cafe.Server.Data;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>Regression tests for the Phase 4 code review fixes that need the full application.</summary>
public class TvReviewFixTests : SessionTestBase
{
    private async Task<int> PairedConsoleAsync(string ip = "10.0.0.5")
    {
        int id;
        using (var scope = Factory.Services.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
            {
                Name = "PS5-01", StationTypeId = await TypeAsync(), Kind = StationKind.Console, TvIpAddress = ip, TvMacAddress = "AA:BB:CC:DD:EE:FF",
            });
            Assert.True(created.Success, created.Error);
            id = created.Value!.Id;
        }

        await using var db = await DbAsync();
        await db.Stations.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.TvToken, "SECRET"));
        return id;
    }

    private async Task<Station> LoadAsync(int id)
    {
        await using var db = await DbAsync();
        return await db.Stations.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private async Task UpdateAsync(int id, Action<StationInput> change)
    {
        var current = await LoadAsync(id);
        var input = new StationInput
        {
            Name = current.Name,
            StationTypeId = current.StationTypeId,
            Kind = current.Kind,
            TvIpAddress = current.TvIpAddress,
            TvMacAddress = current.TvMacAddress,
            AgentMachineId = current.AgentMachineId,
            SortOrder = current.SortOrder,
            IsEnabled = current.IsEnabled,
        };
        change(input);
        using var scope = Factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<StationService>().UpdateAsync(id, input);
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task Changing_the_tv_ip_clears_the_pairing_token()
    {
        var id = await PairedConsoleAsync();
        await UpdateAsync(id, i => i.TvIpAddress = "10.0.0.6");
        Assert.Null((await LoadAsync(id)).TvToken);
    }

    [Fact]
    public async Task Turning_a_console_into_a_pc_clears_the_pairing_token()
    {
        var id = await PairedConsoleAsync();
        await UpdateAsync(id, i => { i.Kind = StationKind.PC; i.AgentMachineId = "PC-1"; });
        Assert.Null((await LoadAsync(id)).TvToken);
    }

    [Fact]
    public async Task Unrelated_edits_keep_the_pairing_token()
    {
        var id = await PairedConsoleAsync();
        await UpdateAsync(id, i => { i.Name = "PS5-renamed"; i.TvMacAddress = "AA:BB:CC:DD:EE:00"; });
        Assert.Equal("SECRET", (await LoadAsync(id)).TvToken);
    }
}
