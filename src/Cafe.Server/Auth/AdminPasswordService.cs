using Cafe.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Auth;

/// <summary>Stores and verifies the single shared admin password (hash only, never plaintext).</summary>
public class AdminPasswordService(IDbContextFactory<CafeDbContext> dbFactory)
{
    public const string HashKey = "AdminPasswordHash";

    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object HashUser = new();

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Settings.AnyAsync(s => s.Key == HashKey, ct);
    }

    /// <summary>Sets the password on first run. Returns false (and changes nothing) when one already exists.</summary>
    public async Task<bool> SetPasswordAsync(string password, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Settings.AnyAsync(s => s.Key == HashKey, ct))
        {
            return false;
        }

        db.Settings.Add(new Setting { Key = HashKey, Value = Hasher.HashPassword(HashUser, password) });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Stations.InputValidation.IsUniqueViolation(ex))
        {
            // A concurrent request configured the password first (primary key conflict); never overwrite.
            // Any other failure (locked, read-only or full disk) propagates so it is not mistaken for "already configured".
            return false;
        }

        return true;
    }

    public async Task<bool> VerifyAsync(string password, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hash = await db.Settings.Where(s => s.Key == HashKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (hash is null)
        {
            return false;
        }

        var result = Hasher.VerifyHashedPassword(HashUser, hash, password);
        return result != PasswordVerificationResult.Failed;
    }
}
