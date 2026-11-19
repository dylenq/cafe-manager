using Cafe.Server.Data;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class DataModelTests
{
    private static IDbContextFactory<CafeDbContext> Db(CafeWebApplicationFactory f) =>
        f.Services.GetRequiredService<IDbContextFactory<CafeDbContext>>();

    [Theory]
    [InlineData("7500.50")]
    [InlineData("0.01")]
    [InlineData("12345678.99")]
    public async Task HourlyRate_round_trips_exactly(string rate)
    {
        using var factory = new CafeWebApplicationFactory();
        var expected = decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture);
        int id;
        await using (var db = await Db(factory).CreateDbContextAsync())
        {
            var type = new StationType { Name = "PC", HourlyRate = expected };
            db.StationTypes.Add(type);
            await db.SaveChangesAsync();
            id = type.Id;
        }

        await using var read = await Db(factory).CreateDbContextAsync();
        var loaded = await read.StationTypes.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.Equal(expected, loaded.HourlyRate);
    }

    [Fact]
    public async Task StationType_names_are_unique_ignoring_case()
    {
        using var factory = new CafeWebApplicationFactory();
        await using var db = await Db(factory).CreateDbContextAsync();
        db.StationTypes.Add(new StationType { Name = "PC", HourlyRate = 1m });
        await db.SaveChangesAsync();

        await using var db2 = await Db(factory).CreateDbContextAsync();
        db2.StationTypes.Add(new StationType { Name = "pc", HourlyRate = 1m });
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task Station_names_are_unique_ignoring_case()
    {
        using var factory = new CafeWebApplicationFactory();
        await using var db = await Db(factory).CreateDbContextAsync();
        var type = new StationType { Name = "PC", HourlyRate = 1m };
        db.Stations.Add(new Station { Name = "PC-01", StationType = type });
        await db.SaveChangesAsync();

        await using var db2 = await Db(factory).CreateDbContextAsync();
        db2.Stations.Add(new Station { Name = "pc-01", StationTypeId = type.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_a_station_type_with_stations_is_blocked()
    {
        using var factory = new CafeWebApplicationFactory();
        int typeId;
        await using (var db = await Db(factory).CreateDbContextAsync())
        {
            var type = new StationType { Name = "PC", HourlyRate = 5m };
            db.Stations.Add(new Station { Name = "PC-01", StationType = type });
            await db.SaveChangesAsync();
            typeId = type.Id;
        }

        await using var del = await Db(factory).CreateDbContextAsync();
        del.StationTypes.Remove(await del.StationTypes.SingleAsync(t => t.Id == typeId));
        await Assert.ThrowsAsync<DbUpdateException>(() => del.SaveChangesAsync());

        await using var check = await Db(factory).CreateDbContextAsync();
        Assert.Equal(1, await check.StationTypes.CountAsync());
        Assert.Equal(1, await check.Stations.CountAsync());
    }

    [Fact]
    public async Task Station_defaults_enabled_and_kind_round_trips()
    {
        using var factory = new CafeWebApplicationFactory();
        int id;
        await using (var db = await Db(factory).CreateDbContextAsync())
        {
            var station = new Station
            {
                Name = "TV-01",
                Kind = StationKind.Console,
                StationType = new StationType { Name = "PS5", HourlyRate = 9000m },
            };
            db.Stations.Add(station);
            await db.SaveChangesAsync();
            id = station.Id;
        }

        await using var read = await Db(factory).CreateDbContextAsync();
        var loaded = await read.Stations.AsNoTracking().SingleAsync(s => s.Id == id);
        Assert.True(loaded.IsEnabled);
        Assert.Equal(StationKind.Console, loaded.Kind);
    }

    [Fact]
    public async Task Migrations_create_history_table_and_all_tables()
    {
        using var factory = new CafeWebApplicationFactory();
        await using var db = await Db(factory).CreateDbContextAsync();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();

        Assert.Contains(applied, m => m.EndsWith("_InitialCreate"));
        Assert.Contains(applied, m => m.EndsWith("_AddStations"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
