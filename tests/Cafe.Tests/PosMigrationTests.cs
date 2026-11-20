using System.Globalization;
using Cafe.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>T-06-05: the AddPos migration moves Phase 5 payments into allocations and seeds the billing start.</summary>
public sealed class PosMigrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cafe-mig-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var p in new[] { _path, _path + "-shm", _path + "-wal" })
        {
            try
            {
                File.Delete(p);
            }
            catch (IOException)
            {
            }
        }
    }

    private CafeDbContext NewContext() =>
        new(new DbContextOptionsBuilder<CafeDbContext>().UseSqlite($"Data Source={_path}").Options);

    [Fact]
    public async Task AddPos_copies_phase5_payments_into_allocations_and_seeds_billing_start()
    {
        await using var db = NewContext();
        var migrations = db.Database.GetMigrations().ToList();
        Assert.EndsWith("_AddPos", migrations[^1]);
        await db.GetService<IMigrator>().MigrateAsync(migrations[^2]);

        await using (var raw = new SqliteConnection($"Data Source={_path}"))
        {
            await raw.OpenAsync();
            await using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                PRAGMA foreign_keys = OFF;
                INSERT INTO Payments (Id, Amount, Method, AtUtc, PackageSaleId, SessionId, Note) VALUES (1, '15000', 0, 1, 7, NULL, NULL);
                INSERT INTO Payments (Id, Amount, Method, AtUtc, PackageSaleId, SessionId, Note) VALUES (2, '6000.50', 1, 2, NULL, 9, NULL);
                INSERT INTO Payments (Id, Amount, Method, AtUtc, PackageSaleId, SessionId, Note) VALUES (3, '100', 0, 3, NULL, NULL, 'loose');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await db.GetService<IMigrator>().MigrateAsync();

        await using var read = NewContext();
        var allocations = await read.PaymentAllocations.AsNoTracking().OrderBy(a => a.PaymentId).ToListAsync();
        Assert.Equal(2, allocations.Count);
        Assert.Equal((1, AllocationTargetType.PackageSale, 7, 15000m),
            (allocations[0].PaymentId, allocations[0].TargetType, allocations[0].TargetId, allocations[0].Amount));
        Assert.Equal((2, AllocationTargetType.Session, 9, 6000.50m),
            (allocations[1].PaymentId, allocations[1].TargetType, allocations[1].TargetId, allocations[1].Amount));
        Assert.DoesNotContain(allocations, a => a.PaymentId == 3);

        var setting = await read.Settings.AsNoTracking().SingleAsync(s => s.Key == "Pos:BillingStartUtc");
        var parsed = DateTimeOffset.Parse(setting.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - parsed).TotalMinutes) < 5);
    }
}
