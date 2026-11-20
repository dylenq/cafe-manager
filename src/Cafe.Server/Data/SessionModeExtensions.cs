namespace Cafe.Server.Data;

public static class SessionModeExtensions
{
    /// <summary>True for modes with a PurchasedMinutes countdown, Grace and expiry (Prepaid and Balance).</summary>
    public static bool IsTimed(this SessionMode mode) => mode is SessionMode.Prepaid or SessionMode.Balance;
}
