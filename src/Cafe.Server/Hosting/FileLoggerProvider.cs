using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cafe.Server.Hosting;

/// <summary>
/// Minimal daily-file logger (cafe-yyyyMMdd.log) with retention. Never throws. Newlines inside a message are
/// folded onto continuation lines starting with a space so user input cannot forge a log record.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private readonly int _retentionDays;
    private readonly LogLevel _minimumLevel;
    private readonly object _gate = new();
    private DateOnly? _lastTrimDay;
    private bool _directoryReady;
    private DateTimeOffset _nextDirectoryAttempt = DateTimeOffset.MinValue;
    private static readonly TimeSpan DirectoryRetryDelay = TimeSpan.FromSeconds(30);

    public FileLoggerProvider(string directory, Func<DateTimeOffset> now, int retentionDays = 14, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory;
        _now = now;
        _retentionDays = retentionDays;
        _minimumLevel = minimumLevel;
        lock (_gate)
        {
            TrimOldFiles(DateOnly.FromDateTime(SafeNow().DateTime));
        }
    }

    /// <summary>The most recent write failure (null once a write succeeds), so a caller can surface "logs are not being written".</summary>
    public string? LastError { get; private set; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private DateTimeOffset SafeNow()
    {
        try
        {
            return _now();
        }
        catch (Exception)
        {
            return DateTimeOffset.Now;
        }
    }

    private void Write(DateTimeOffset at, string text)
    {
        try
        {
            lock (_gate)
            {
                var day = DateOnly.FromDateTime(at.DateTime);
                if (!EnsureDirectory(at))
                {
                    return;
                }

                if (_lastTrimDay != day)
                {
                    TrimOldFiles(day);
                }

                var path = Path.Combine(_directory, $"cafe-{day:yyyyMMdd}.log");
                try
                {
                    File.AppendAllText(path, text + Environment.NewLine);
                    LastError = null;
                }
                catch (DirectoryNotFoundException)
                {
                    // The folder was removed under us; recreate it on the next write.
                    _directoryReady = false;
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            // Logging must never take the server down, but do not fail silently either.
            LastError = ex.Message;
            Trace.WriteLine($"FileLoggerProvider: could not write to '{_directory}': {ex.Message}");
        }
    }

    // Creates the log folder once; after a failure it is retried at most every 30 seconds.
    private bool EnsureDirectory(DateTimeOffset at)
    {
        if (_directoryReady)
        {
            return true;
        }

        if (at < _nextDirectoryAttempt)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            _directoryReady = true;
            return true;
        }
        catch (Exception ex)
        {
            _nextDirectoryAttempt = at + DirectoryRetryDelay;
            LastError = ex.Message;
            Trace.WriteLine($"FileLoggerProvider: could not create '{_directory}': {ex.Message}");
            return false;
        }
    }

    private void TrimOldFiles(DateOnly today)
    {
        _lastTrimDay = today;
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            var cutoff = today.AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "cafe-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length == 13
                    && DateOnly.TryParseExact(name.AsSpan(5), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    && date < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            // Best effort.
            Trace.WriteLine($"FileLoggerProvider: could not trim old logs in '{_directory}': {ex.Message}");
        }
    }

    private static string Fold(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine + " ");

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner._minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                var at = owner.SafeNow();
                var sb = new StringBuilder();
                sb.Append(at.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                    .Append(" [").Append(logLevel).Append("] ").Append(Fold(category)).Append(": ").Append(Fold(formatter(state, exception)));
                if (exception is not null)
                {
                    sb.Append(Environment.NewLine).Append(' ').Append(Fold(exception.ToString()));
                }

                owner.Write(at, sb.ToString());
            }
            catch (Exception)
            {
                // Never throw from logging.
            }
        }
    }
}
