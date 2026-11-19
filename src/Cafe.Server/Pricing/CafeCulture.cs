using System.Globalization;

namespace Cafe.Server.Pricing;

public static class CafeCulture
{
    public const string Key = "Cafe:Culture";

    /// <summary>
    /// Resolves the configured culture name (e.g. "en-US", "vi-VN") used for currency and number formatting.
    /// Empty or unknown names give null, meaning "keep the server's culture".
    /// </summary>
    public static CultureInfo? Resolve(string? configured, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(configured.Trim(), predefinedOnly: true);
        }
        catch (CultureNotFoundException ex)
        {
            logger?.LogWarning(ex, "{Key} '{Culture}' is not a known culture; using the server's culture.", Key, configured);
            return null;
        }
    }

    /// <summary>Applies the culture as the default for all threads (and circuits) so formatting is predictable.</summary>
    public static void Apply(CultureInfo? culture)
    {
        if (culture is null)
        {
            return;
        }

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
