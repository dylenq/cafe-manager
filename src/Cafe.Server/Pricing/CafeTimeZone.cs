namespace Cafe.Server.Pricing;

public static class CafeTimeZone
{
    /// <summary>Resolves the configured zone id (IANA or Windows). Empty means the server's local zone; failures fall back to it.</summary>
    public static TimeZoneInfo Resolve(string? configured, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(configured.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger?.LogWarning(ex, "Cafe:TimeZone '{TimeZone}' could not be resolved; using the server's local time zone.", configured);
            return TimeZoneInfo.Local;
        }
    }
}
