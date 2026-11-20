using System.Globalization;

namespace Cafe.Server.Backups;

/// <summary>Backup settings read from configuration (Backup:Hour, Backup:Keep, Backup:RunScheduler, Backup:Directory).</summary>
public sealed record BackupOptions(int Hour, int Keep, bool RunScheduler, string? Directory)
{
    public const int DefaultHour = 4;
    public const int DefaultKeep = 14;

    /// <summary>Reads the options; out-of-range values fall back to the defaults and are reported through <paramref name="warn"/>.</summary>
    public static BackupOptions From(IConfiguration configuration, Action<string>? warn = null)
    {
        var hour = ParseInt(configuration["Backup:Hour"], DefaultHour, "Backup:Hour", warn);
        if (hour is < 0 or > 23)
        {
            warn?.Invoke($"Backup:Hour {hour} is outside 0-23; using {DefaultHour}.");
            hour = DefaultHour;
        }

        var keep = ParseInt(configuration["Backup:Keep"], DefaultKeep, "Backup:Keep", warn);
        if (keep < 1)
        {
            warn?.Invoke($"Backup:Keep {keep} is below 1; using 1.");
            keep = 1;
        }

        var runScheduler = true;
        var schedulerText = configuration["Backup:RunScheduler"];
        if (!string.IsNullOrWhiteSpace(schedulerText) && !bool.TryParse(schedulerText.Trim(), out runScheduler))
        {
            warn?.Invoke($"Backup:RunScheduler '{schedulerText}' is not true or false; using true.");
            runScheduler = true;
        }

        var directory = configuration["Backup:Directory"];
        return new BackupOptions(
            hour,
            keep,
            runScheduler,
            string.IsNullOrWhiteSpace(directory) ? null : directory.Trim());
    }

    private static int ParseInt(string? text, int fallback, string key, Action<string>? warn)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        warn?.Invoke($"{key} '{text}' is not a whole number; using {fallback}.");
        return fallback;
    }
}
