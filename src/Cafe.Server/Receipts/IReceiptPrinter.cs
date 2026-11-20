namespace Cafe.Server.Receipts;

public sealed record PrintResult(bool Success, string? Error)
{
    public static PrintResult Ok() => new(true, null);

    public static PrintResult Fail(string error) => new(false, error);
}

/// <summary>Sends finished ESC/POS bytes to a device. Implementations return a failed result instead of throwing.</summary>
public interface IReceiptPrinter
{
    Task<PrintResult> SendAsync(byte[] data, ReceiptSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>The raw-TCP printer slot (swappable in tests).</summary>
public interface INetworkReceiptPrinter : IReceiptPrinter;

/// <summary>The Windows spooler printer slot (swappable in tests).</summary>
public interface IWindowsReceiptPrinter : IReceiptPrinter;
