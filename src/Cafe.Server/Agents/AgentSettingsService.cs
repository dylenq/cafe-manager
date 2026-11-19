using System.Security.Cryptography;
using Cafe.Server.Data;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Agents;

/// <summary>The shared agent key (Agents:SharedKey) and the cafe name shown on lock screens.</summary>
public sealed class AgentSettingsService(IDbContextFactory<CafeDbContext> dbFactory, AgentRegistry registry)
{
    public const string SharedKeyKey = "Agents:SharedKey";
    public const string CafeNameKey = "Cafe:Name";
    public const string DefaultCafeName = "Cafe Manager";

    private volatile string? _cachedKey;

    public async Task<string> GetOrCreateSharedKeyAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedKey is { } cached)
        {
            return cached;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SharedKeyKey, cancellationToken);
            if (row is not null)
            {
                _cachedKey = row.Value;
                return row.Value;
            }

            db.Settings.Add(new Setting { Key = SharedKeyKey, Value = NewKey() });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (InputValidation.IsUniqueViolation(ex))
            {
                // A concurrent caller created it first; read theirs on the next pass.
                continue;
            }
        }

        await using var last = await dbFactory.CreateDbContextAsync(cancellationToken);
        var value = (await last.Settings.AsNoTracking().FirstAsync(s => s.Key == SharedKeyKey, cancellationToken)).Value;
        _cachedKey = value;
        return value;
    }

    public async Task<string> GetCafeNameAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == CafeNameKey, cancellationToken);
        return string.IsNullOrWhiteSpace(row?.Value) ? DefaultCafeName : row.Value;
    }

    private static string NewKey() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
