using System.Net;
using Cafe.Server.Backups;
using Cafe.Server.Hosting;
using Cafe.Server.Pricing;
using Cafe.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cafe.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cafe-backup-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));
    private readonly string _dbPath;

    public BackupServiceTests()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "live.db");
        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE Thing (Id INTEGER PRIMARY KEY, Name TEXT); INSERT INTO Thing (Name) VALUES ('a'),('b'),('c');";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private string BackupDir => Path.Combine(_root, "backups");

    private BackupService Create(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Cafe:TimeZone"] = "UTC",
            ["Backup:Directory"] = BackupDir,
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var location = new DataLocation($"Data Source={_dbPath}", _root, Path.Combine(_root, "logs"), BackupDir, false);
        return new BackupService(location, config, _clock, new PricingConfigService(config, NullLogger<PricingConfigService>.Instance), NullLogger<BackupService>.Instance);
    }

    private static long CountRows(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Thing";
        return (long)cmd.ExecuteScalar()!;
    }

    private static string[] Names(string dir) =>
        Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    [Fact]
    public async Task Backup_creates_a_named_consistent_copy()
    {
        var service = Create();

        var result = await service.BackupNowAsync();

        Assert.True(result.Success);
        Assert.Equal("cafe-20261120-1000.db", result.FileName);
        Assert.Equal(["cafe-20261120-1000.db"], Names(BackupDir));
        Assert.Equal(3, CountRows(Path.Combine(BackupDir, result.FileName!)));
    }

    [Fact]
    public async Task A_second_backup_in_the_same_minute_replaces_the_file()
    {
        var service = Create();
        await service.BackupNowAsync();
        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO Thing (Name) VALUES ('d')";
            cmd.ExecuteNonQuery();
        }

        var result = await service.BackupNowAsync();

        Assert.True(result.Success);
        Assert.Single(Names(BackupDir));
        Assert.Equal(4, CountRows(Path.Combine(BackupDir, result.FileName!)));
    }

    [Fact]
    public async Task Retention_keeps_only_the_newest_and_never_touches_unrelated_files()
    {
        var service = Create(new() { ["Backup:Keep"] = "3" });
        Directory.CreateDirectory(BackupDir);
        File.WriteAllText(Path.Combine(BackupDir, "notes.txt"), "mine");
        File.WriteAllText(Path.Combine(BackupDir, "cafe-old.db"), "mine too");

        for (var i = 0; i < 5; i++)
        {
            await service.BackupNowAsync();
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(
            ["cafe-20261120-1002.db", "cafe-20261120-1003.db", "cafe-20261120-1004.db", "cafe-old.db", "notes.txt"],
            Names(BackupDir));
    }

    [Fact]
    public async Task Keep_below_one_is_treated_as_one()
    {
        var service = Create(new() { ["Backup:Keep"] = "0" });
        await service.BackupNowAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.BackupNowAsync();

        Assert.Equal(["cafe-20261120-1001.db"], Names(BackupDir));
    }

    [Fact]
    public async Task LastBackupUtc_is_null_without_backups_then_the_newest_file_time()
    {
        var service = Create();
        Assert.Null(service.LastBackupUtc);

        await service.BackupNowAsync();
        _clock.Advance(TimeSpan.FromMinutes(5));
        await service.BackupNowAsync();

        Assert.Equal(new DateTimeOffset(2026, 11, 20, 10, 5, 0, TimeSpan.Zero), service.LastBackupUtc);
    }

    [Fact]
    public async Task A_failing_backup_returns_a_generic_failure_and_leaves_no_partial_file()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Cafe:TimeZone"] = "UTC", ["Backup:Directory"] = BackupDir }).Build();
        var location = new DataLocation("Data Source=/nonexistent-dir-xyz/none.db", _root, "l", BackupDir, false);
        var service = new BackupService(location, config, _clock, new PricingConfigService(config, NullLogger<PricingConfigService>.Instance), NullLogger<BackupService>.Instance);

        var result = await service.BackupNowAsync();

        Assert.False(result.Success);
        Assert.Equal("Backup failed. See the server log.", result.Error);
        Assert.DoesNotContain("nonexistent", result.Error);
        Assert.Empty(Names(BackupDir));
    }

    [Fact]
    public void NextRunUtc_is_today_when_still_ahead_and_tomorrow_otherwise()
    {
        var utc = TimeZoneInfo.Utc;

        Assert.Equal(new DateTimeOffset(2026, 11, 20, 4, 0, 0, TimeSpan.Zero), BackupService.NextRunUtc(new(2026, 11, 20, 3, 59, 0, TimeSpan.Zero), utc, 4));
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 4, 0, 0, TimeSpan.Zero), BackupService.NextRunUtc(new(2026, 11, 20, 4, 0, 0, TimeSpan.Zero), utc, 4));
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 4, 0, 0, TimeSpan.Zero), BackupService.NextRunUtc(new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero), utc, 4));
    }

    [Fact]
    public void NextRunUtc_is_correct_across_a_DST_change()
    {
        var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var before = new DateTimeOffset(2025, 3, 9, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2025, 3, 9, 8, 0, 0, TimeSpan.Zero), BackupService.NextRunUtc(before, ny, 4));
        // 02:00 does not exist on 2025-03-09: the first valid local time is 03:00 EDT = 07:00Z.
        Assert.Equal(new DateTimeOffset(2025, 3, 9, 7, 0, 0, TimeSpan.Zero), BackupService.NextRunUtc(before, ny, 2));
    }

    [Fact]
    public void Hour_outside_0_to_23_falls_back_to_4()
    {
        var warnings = new List<string>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Backup:Hour"] = "30" }).Build();

        var options = BackupOptions.From(config, warnings.Add);

        Assert.Equal(4, options.Hour);
        Assert.Single(warnings);
        Assert.Equal(14, options.Keep);
        Assert.True(options.RunScheduler);
    }

    [Fact]
    public async Task Scheduled_loop_runs_at_the_configured_hour()
    {
        var service = Create(new() { ["Backup:Hour"] = "11" });
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100);
            Assert.Null(service.LastBackupUtc);

            _clock.Advance(TimeSpan.FromHours(1));
            for (var i = 0; i < 100 && service.LastBackupUtc is null; i++)
            {
                await Task.Delay(50);
            }

            Assert.NotNull(service.LastBackupUtc);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Disabled_scheduler_never_backs_up()
    {
        var service = Create(new() { ["Backup:Hour"] = "11", ["Backup:RunScheduler"] = "false" });
        await service.StartAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromHours(2));
        await Task.Delay(150);
        await service.StopAsync(CancellationToken.None);

        Assert.Null(service.LastBackupUtc);
    }
}

public class BackupSettingsPageTests
{
    [Fact]
    public async Task Settings_page_offers_Back_up_now_and_shows_the_last_backup_time()
    {
        await using var factory = new CafeWebApplicationFactory();
        using var client = await factory.CreateAuthenticatedClientAsync();

        var before = await client.GetAsync("/settings");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var html = await before.Content.ReadAsStringAsync();
        Assert.Contains("Back up now", html);
        Assert.Contains("Last backup", html);
        Assert.Contains("Never", html);

        var result = await factory.Services.GetRequiredService<BackupService>().BackupNowAsync();
        Assert.True(result.Success);

        var after = await (await client.GetAsync("/settings")).Content.ReadAsStringAsync();
        Assert.Contains(factory.Clock.GetUtcNow().ToString("yyyy-MM-dd HH:mm"), after);
    }
}
