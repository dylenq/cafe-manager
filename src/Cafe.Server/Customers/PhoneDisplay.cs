namespace Cafe.Server.Customers;

/// <summary>Display-only phone grouping. The stored value is never changed.</summary>
public static class PhoneDisplay
{
    public static string Format(string? digits)
    {
        if (string.IsNullOrEmpty(digits))
        {
            return string.Empty;
        }

        return digits.Length > 6 ? $"{digits[..3]} {digits[3..6]} {digits[6..]}" : digits;
    }
}
