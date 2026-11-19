using System.Globalization;
using Cafe.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Sessions;

public class SessionSettingsService(IDbContextFactory<CafeDbContext> dbFactory)
{
    public const string GraceKey = "Sessions:GracePeriodMinutes";
    public const string WarningKey = "Sessions:WarningMinutes";

    public async Task<SessionSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Settings.AsNoTracking()
            .Where(s => s.Key == GraceKey || s.Key == WarningKey)
            .ToListAsync(cancellationToken);

        var grace = SessionSettings.DefaultGraceMinutes;
        var warnings = SessionSettings.DefaultWarningMinutes;
        foreach (var row in rows)
        {
            if (row.Key == GraceKey
                && int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g)
                && g is >= 0 and <= 30)
            {
                grace = g;
            }
            else if (row.Key == WarningKey && TryParseWarnings(row.Value, out var parsed))
            {
                warnings = parsed;
            }
        }

        return new SessionSettings(grace, warnings);
    }

    public static bool TryParseWarnings(string? raw, out IReadOnlyList<int> minutes)
    {
        minutes = SessionSettings.DefaultWarningMinutes;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var list = new List<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) || m is < 1 or > 60)
            {
                return false;
            }

            list.Add(m);
        }

        if (list.Count == 0)
        {
            return false;
        }

        minutes = list.Distinct().OrderByDescending(x => x).ToList();
        return true;
    }
}
