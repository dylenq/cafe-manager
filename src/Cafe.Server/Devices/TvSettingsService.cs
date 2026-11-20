using Cafe.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cafe.Server.Devices;

/// <summary>Editable TV settings kept in the Setting table (the options value is only the default).</summary>
public sealed class TvSettingsService(IDbContextFactory<CafeDbContext> dbFactory, IOptions<TvOptions> options)
{
    public const string PowerOffOnStopKey = "Tv:PowerOffOnStop";

    public async Task<bool> GetPowerOffOnStopAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == PowerOffOnStopKey, cancellationToken);
        return bool.TryParse(row?.Value, out var value) ? value : options.Value.PowerOffOnStop;
    }

    public async Task SetPowerOffOnStopAsync(bool value, CancellationToken cancellationToken = default)
    {
        var text = value ? "true" : "false";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == PowerOffOnStopKey, cancellationToken);
            if (row is null)
            {
                db.Settings.Add(new Setting { Key = PowerOffOnStopKey, Value = text });
            }
            else
            {
                row.Value = text;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // A concurrent save created the row first; update theirs on the next pass.
            }
        }
    }
}
