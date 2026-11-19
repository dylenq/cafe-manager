using System.Security.Cryptography;

namespace Cafe.Agent.Core;

/// <summary>PBKDF2-SHA256 hash and verify for the emergency unlock code. An empty stored hash disables the feature.</summary>
public static class EmergencyCodeHasher
{
    private const string Algorithm = "pbkdf2-sha256";
    private const int DefaultIterations = 200_000;
    private const int MinIterations = 100_000;
    private const int MaxIterations = 5_000_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    public static string Hash(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(code, salt, DefaultIterations, HashAlgorithmName.SHA256, KeyBytes);
        return $"{Algorithm}${DefaultIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string? code, string? stored)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        var parts = stored.Trim().Split('$');
        if (parts.Length != 4 || parts[0] != Algorithm
            || !int.TryParse(parts[1], out var iterations) || iterations < MinIterations || iterations > MaxIterations)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length == 0 || expected.Length == 0)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(code, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
