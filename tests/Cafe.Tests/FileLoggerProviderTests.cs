using Cafe.Server.Hosting;
using Microsoft.Extensions.Logging;

namespace Cafe.Tests;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cafe-log-{Guid.NewGuid():N}");
    private DateTimeOffset _now = new(2026, 11, 20, 14, 3, 7, 123, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private FileLoggerProvider Create(int retention = 14) => new(_dir, () => _now, retention);

    [Fact]
    public void Writes_a_daily_file_with_the_documented_line_format()
    {
        using var provider = Create();
        provider.CreateLogger("Cafe.Test").LogInformation("hello {Name}", "world");

        var file = Path.Combine(_dir, "cafe-20261120.log");
        Assert.Equal(["2026-11-20 14:03:07.123 [Information] Cafe.Test: hello world"], File.ReadAllLines(file));
    }

    [Fact]
    public void Below_minimum_level_is_not_written()
    {
        using var provider = Create();
        provider.CreateLogger("c").LogDebug("quiet");

        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Exceptions_follow_on_continuation_lines()
    {
        using var provider = Create();
        provider.CreateLogger("c").LogError(new InvalidOperationException("boom"), "failed");

        var lines = File.ReadAllLines(Path.Combine(_dir, "cafe-20261120.log"));
        Assert.StartsWith("2026-11-20 14:03:07.123 [Error] c: failed", lines[0]);
        Assert.All(lines.Skip(1), l => Assert.StartsWith(" ", l));
        Assert.Contains(lines, l => l.Contains("boom"));
    }

    [Fact]
    public void Newlines_in_a_message_cannot_forge_a_record()
    {
        using var provider = Create();
        provider.CreateLogger("c").LogInformation("{Text}", "first\n2026-11-20 00:00:00.000 [Critical] fake: x\r\nlast");

        var lines = File.ReadAllLines(Path.Combine(_dir, "cafe-20261120.log"));
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("2026-", lines[0]);
        Assert.All(lines.Skip(1), l => Assert.StartsWith(" ", l));
    }

    [Fact]
    public void Retention_deletes_files_older_than_14_days_but_keeps_day_14()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "cafe-20261105.log"), "old");   // 15 days old
        File.WriteAllText(Path.Combine(_dir, "cafe-20261106.log"), "edge");  // exactly 14 days old
        File.WriteAllText(Path.Combine(_dir, "other.log"), "keep");

        using var provider = Create();

        Assert.False(File.Exists(Path.Combine(_dir, "cafe-20261105.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "cafe-20261106.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "other.log")));
    }

    [Fact]
    public void Rollover_writes_a_new_file_and_trims_again()
    {
        using var provider = Create();
        var logger = provider.CreateLogger("c");
        logger.LogInformation("day one");
        File.WriteAllText(Path.Combine(_dir, "cafe-20261106.log"), "edge");

        _now = _now.AddDays(1);
        logger.LogInformation("day two");

        Assert.True(File.Exists(Path.Combine(_dir, "cafe-20261121.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "cafe-20261120.log")));
        Assert.False(File.Exists(Path.Combine(_dir, "cafe-20261106.log")));
    }

    [Fact]
    public void An_unwritable_directory_does_not_throw()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"cafe-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "a file, not a folder");
        try
        {
            using var provider = new FileLoggerProvider(Path.Combine(blocker, "logs"), () => _now);
            provider.CreateLogger("c").LogInformation("still fine");
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
