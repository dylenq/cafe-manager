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

        // Real parent rows (these tables are unchanged by AddPos) so the Payments table rebuild passes FK checks.
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var type = new StationType { Name = "PC", HourlyRate = 1m };
        var station = new Station { Name = "PC-01", StationType = type };
        var customer = new Customer { Name = "Alice", CreatedAtUtc = at };
        var package = new TimePackage { Name = "Pack", Minutes = 60, Price = 15000m };
        var sale = new PackageSale { Customer = customer, TimePackage = package, Price = 15000m, Minutes = 60, AtUtc = at };
        var session = new Session { Station = station, StartedAtUtc = at, Status = SessionStatus.Ended, ChargedAmount = 6000.50m };
        await using (var seed = NewContext())
        {
            seed.AddRange(sale, session);
            await seed.SaveChangesAsync();
        }

        await using (var raw = new SqliteConnection($"Data Source={_path}"))
        {
            await raw.OpenAsync();
            await using var cmd = raw.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO Payments (Id, Amount, Method, AtUtc, PackageSaleId, SessionId, Note) VALUES (1, '15000', 0, 1, {sale.Id}, NULL, NULL);
                INSERT INTO Payments (Id, Amount, Method, AtUtc, PackageSaleId, SessionId, Note) VALUES (2, '6000.50', 1, 2, NULL, {session.Id}, NULL);
                INSERT INTO Payments (Id, Amount, Method, AtUtc, PackageSaleId, SessionId, Note) VALUES (3, '100', 0, 3, NULL, NULL, 'loose');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await db.GetService<IMigrator>().MigrateAsync();

        await using var read = NewContext();
        var allocations = await read.PaymentAllocations.AsNoTracking().OrderBy(a => a.PaymentId).ToListAsync();
        Assert.Equal(2, allocations.Count);
        Assert.Equal((1, AllocationTargetType.PackageSale, sale.Id, 15000m),
            (allocations[0].PaymentId, allocations[0].TargetType, allocations[0].TargetId, allocations[0].Amount));
        Assert.Equal((2, AllocationTargetType.Session, session.Id, 6000.50m),
            (allocations[1].PaymentId, allocations[1].TargetType, allocations[1].TargetId, allocations[1].Amount));
        Assert.DoesNotContain(allocations, a => a.PaymentId == 3);

        var setting = await read.Settings.AsNoTracking().SingleAsync(s => s.Key == "Pos:BillingStartUtc");
        var parsed = DateTimeOffset.Parse(setting.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - parsed).TotalMinutes) < 5);
    }
}
