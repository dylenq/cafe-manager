using System.Globalization;
using Cafe.Server.Hosting;
using Cafe.Server.Pricing;
using Microsoft.Data.Sqlite;

namespace Cafe.Server.Backups;

/// <summary>Outcome of one backup run. Failure carries a generic message, never a path or exception text.</summary>
public sealed record BackupResult(bool Success, string? FileName, DateTimeOffset? AtUtc, string? Error);

/// <summary>
/// Copies the SQLite database to cafe-YYYYMMDD-HHmm.db in the backup folder every day at Backup:Hour (cafe local time)
/// and keeps the newest Backup:Keep copies. Also registered as a singleton so Settings can trigger "Back up now".
/// </summary>
public sealed class BackupService : BackgroundService
{
    private const string FilePattern = "cafe-????????-????.db";
    private const string NameFormat = "yyyyMMdd-HHmm";

    private readonly DataLocation _location;
    private readonly TimeProvider _time;
    private readonly PricingConfigService _pricing;
    private readonly ILogger<BackupService> _logger;
    private readonly BackupOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupService(
        DataLocation location,
        IConfiguration configuration,
        TimeProvider time,
        PricingConfigService pricing,
        ILogger<BackupService> logger)
    {
        _location = location;
        _time = time;
        _pricing = pricing;
        _logger = logger;
        _options = BackupOptions.From(configuration, message => logger.LogWarning("{Message}", message));
    }

    public string Directory => _options.Directory ?? _location.BackupDirectory;

    public int Keep => _options.Keep;

    public int Hour => _options.Hour;

    /// <summary>Timestamp of the newest backup file (name read in the cafe zone, last write time as fallback), or null.</summary>
    public DateTimeOffset? LastBackupUtc
    {
        get
        {
            try
            {
                var newest = BackupFiles().OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault();
                if (newest is null)
                {
                    return null;
                }

                var name = Path.GetFileNameWithoutExtension(newest)[5..];
                if (DateTime.TryParseExact(name, NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                {
                    var zone = _pricing.TimeZone;
                    if (zone.IsInvalidTime(local))
                    {
                        local = local.AddHours(1);
                    }

                    return new DateTimeOffset(local, OffsetFor(zone, local)).ToUniversalTime();
                }

                return new DateTimeOffset(File.GetLastWriteTimeUtc(newest), TimeSpan.Zero);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the backup folder.");
                return null;
            }
        }
    }

    /// <summary>The next local Hour:00 strictly after <paramref name="nowUtc"/>; a time inside a DST gap moves to the first valid time.</summary>
    public static DateTimeOffset NextRunUtc(DateTimeOffset nowUtc, TimeZoneInfo zone, int hour)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime;
        var date = localNow.Date;
        for (var i = 0; i < 3; i++)
        {
            var candidate = date.AddHours(hour);
            while (zone.IsInvalidTime(candidate))
            {
                candidate = candidate.AddMinutes(30);
            }

            var utc = new DateTimeOffset(candidate, OffsetFor(zone, candidate)).ToUniversalTime();
            if (utc > nowUtc)
            {
                return utc;
            }

            date = date.AddDays(1);
        }

        return nowUtc.AddDays(1);
    }

    private static TimeSpan OffsetFor(TimeZoneInfo zone, DateTime unspecifiedLocal) =>
        zone.IsAmbiguousTime(unspecifiedLocal)
            ? zone.GetAmbiguousTimeOffsets(unspecifiedLocal).Max()
            : zone.GetUtcOffset(unspecifiedLocal);

    /// <summary>Makes one backup now. Never throws; a failure is logged and reported as a generic message.</summary>
    public async Task<BackupResult> BackupNowAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        string? temp = null;
        try
        {
            var directory = Directory;
            System.IO.Directory.CreateDirectory(directory);

            var nowUtc = _time.GetUtcNow();
            var local = TimeZoneInfo.ConvertTime(nowUtc, _pricing.TimeZone);
            var fileName = $"cafe-{local.ToString(NameFormat, CultureInfo.InvariantCulture)}.db";
            var final = Path.Combine(directory, fileName);
            temp = Path.Combine(directory, $"cafe-backup-{Guid.NewGuid():N}.tmp");

            // BackupDatabase gives a consistent copy even while the server is writing. The copy goes to a temp name and
            // is moved over the final name so a crash never leaves a half file that looks like a backup.
            var sourceString = new SqliteConnectionStringBuilder(_location.ConnectionString) { Pooling = false }.ToString();
            using (var source = new SqliteConnection(sourceString))
            using (var destination = new SqliteConnection($"Data Source={temp};Pooling=False"))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
                SqliteConnection.ClearPool(source);
                SqliteConnection.ClearPool(destination);
            }

            File.Move(temp, final, overwrite: true);
            temp = null;
            Prune(directory);
            _logger.LogInformation("Database backup written: {FileName}", fileName);
            return new BackupResult(true, fileName, nowUtc, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database backup failed.");
            return new BackupResult(false, null, null, "Backup failed. See the server log.");
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception)
                {
                    // Best effort.
                }
            }

            _gate.Release();
        }
    }

    private IEnumerable<string> BackupFiles()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(Directory, FilePattern).Where(IsBackupName);
    }

    private static bool IsBackupName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length == 18
            && DateTime.TryParseExact(name[5..], NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private void Prune(string directory)
    {
        try
        {
            var stale = System.IO.Directory.EnumerateFiles(directory, FilePattern)
                .Where(IsBackupName)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Skip(_options.Keep);
            foreach (var file in stale)
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not prune old backups.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RunScheduler)
        {
            return;
        }

        var zone = _pricing.TimeZone;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var next = NextRunUtc(_time.GetUtcNow(), zone, _options.Hour);
                var delay = next - _time.GetUtcNow();
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _time, stoppingToken);
                }

                var result = await BackupNowAsync(stoppingToken);
                if (!result.Success)
                {
                    _logger.LogError("Nightly backup failed; trying again at the next scheduled time.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup scheduler error; waiting for the next scheduled time.");
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), _time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
