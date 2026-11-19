using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Cafe.Agent;

/// <summary>Minimal file logger writing logs/agent-yyyyMMdd.log next to the exe. Never throws.</summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetentionDays = 14;

    private readonly string _directory;
    private readonly object _gate = new();

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        TrimOldFiles();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"agent-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // Logging must never take the agent down.
        }
    }

    private void TrimOldFiles()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "agent-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                var message = formatter(state, exception);
                var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {logLevel} {category} {message}");
                if (exception is not null)
                {
                    line += " | " + exception.GetType().Name + ": " + exception.Message;
                }

                owner.Write(line);
            }
            catch (Exception)
            {
                // Never throw from logging.
            }
        }
    }
}
