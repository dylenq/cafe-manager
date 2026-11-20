using System.ComponentModel.DataAnnotations;

namespace Cafe.Server.Receipts;

/// <summary>How receipts are printed. Explicit values.</summary>
public enum ReceiptMode
{
    None = 0,
    Network = 1,
    WindowsPrinter = 2,
    Browser = 3,
}

public sealed record ReceiptSettings(
    ReceiptMode Mode,
    string Host,
    int Port,
    string WindowsPrinter,
    int Columns,
    string CafeName,
    string Address,
    string Phone,
    string Footer)
{
    public const int DefaultPort = 9100;
    public const int DefaultColumns = 42;

    public static ReceiptSettings Default { get; } =
        new(ReceiptMode.Browser, string.Empty, DefaultPort, string.Empty, DefaultColumns, "Cafe", string.Empty, string.Empty, "Thank you!");
}

public class ReceiptSettingsInput
{
    public ReceiptMode Mode { get; set; } = ReceiptMode.Browser;

    [MaxLength(255, ErrorMessage = "Host is too long.")]
    public string? Host { get; set; }

    [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535.")]
    public int Port { get; set; } = ReceiptSettings.DefaultPort;

    [MaxLength(200, ErrorMessage = "Windows printer name is too long.")]
    public string? WindowsPrinter { get; set; }

    public int Columns { get; set; } = ReceiptSettings.DefaultColumns;

    [MaxLength(60, ErrorMessage = "Cafe name must be 60 characters or fewer.")]
    public string? CafeName { get; set; }

    [MaxLength(100, ErrorMessage = "Address must be 100 characters or fewer.")]
    public string? Address { get; set; }

    [MaxLength(40, ErrorMessage = "Phone must be 40 characters or fewer.")]
    public string? Phone { get; set; }

    [MaxLength(120, ErrorMessage = "Footer must be 120 characters or fewer.")]
    public string? Footer { get; set; }
}
