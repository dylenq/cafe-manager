namespace Cafe.Server.Backups;

/// <summary>Backup settings read from configuration (Backup:Hour, Backup:Keep, Backup:RunScheduler, Backup:Directory).</summary>
public sealed record BackupOptions(int Hour, int Keep, bool RunScheduler, string? Directory)
{
    public const int DefaultHour = 4;
    public const int DefaultKeep = 14;

    /// <summary>Reads the options; out-of-range values fall back to the defaults and are reported through <paramref name="warn"/>.</summary>
    public static BackupOptions From(IConfiguration configuration, Action<string>? warn = null)
    {
        var hour = configuration.GetValue("Backup:Hour", DefaultHour);
        if (hour is < 0 or > 23)
        {
            warn?.Invoke($"Backup:Hour {hour} is outside 0-23; using {DefaultHour}.");
            hour = DefaultHour;
        }

        var keep = configuration.GetValue("Backup:Keep", DefaultKeep);
        if (keep < 1)
        {
            warn?.Invoke($"Backup:Keep {keep} is below 1; using 1.");
            keep = 1;
        }

        var directory = configuration["Backup:Directory"];
        return new BackupOptions(
            hour,
            keep,
            configuration.GetValue("Backup:RunScheduler", true),
            string.IsNullOrWhiteSpace(directory) ? null : directory.Trim());
    }
}
