using System.Globalization;
using Cafe.Server.Data;
using Cafe.Server.Stations;
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

    public async Task<ServiceResult> SaveAsync(SessionSettingsInput input, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string>();
        if (input.GracePeriodMinutes is < 0 or > 30)
        {
            errors[nameof(SessionSettingsInput.GracePeriodMinutes)] = "Grace period must be between 0 and 30 minutes.";
        }

        IReadOnlyList<int> warnings = [];
        if (!TryParseWarnings(input.WarningMinutes, out warnings))
        {
            errors[nameof(SessionSettingsInput.WarningMinutes)] =
                "Warning minutes must be a comma-separated list of whole numbers from 1 to 60, for example 10,5,1.";
        }

        if (errors.Count > 0)
        {
            return ServiceResult.Fail(InputValidation.Summarise(errors), errors);
        }

        var graceValue = input.GracePeriodMinutes.ToString(CultureInfo.InvariantCulture);
        var warningValue = string.Join(",", warnings.Select(w => w.ToString(CultureInfo.InvariantCulture)));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Settings
            .Where(s => s.Key == GraceKey || s.Key == WarningKey)
            .ToDictionaryAsync(s => s.Key, cancellationToken);
        Upsert(db, rows, GraceKey, graceValue);
        Upsert(db, rows, WarningKey, warningValue);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two saves raced to insert the same key; nothing was stored by this call.
            return ServiceResult.Fail("The settings were changed at the same time. Try again.");
        }

        return ServiceResult.Ok();
    }

    private static void Upsert(CafeDbContext db, Dictionary<string, Setting> rows, string key, string value)
    {
        if (rows.TryGetValue(key, out var row))
        {
            row.Value = value;
        }
        else
        {
            db.Settings.Add(new Setting { Key = key, Value = value });
        }
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
