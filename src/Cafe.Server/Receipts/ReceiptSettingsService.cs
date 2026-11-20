using System.Globalization;
using Cafe.Server.Data;
using Cafe.Server.Stations;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Receipts;

/// <summary>Receipt settings in the Settings table (keys Receipts:*), with validation.</summary>
public class ReceiptSettingsService(IDbContextFactory<CafeDbContext> dbFactory)
{
    public const string ModeKey = "Receipts:Mode";
    public const string HostKey = "Receipts:Host";
    public const string PortKey = "Receipts:Port";
    public const string WindowsPrinterKey = "Receipts:WindowsPrinter";
    public const string ColumnsKey = "Receipts:Columns";
    public const string CafeNameKey = "Receipts:CafeName";
    public const string AddressKey = "Receipts:Address";
    public const string PhoneKey = "Receipts:Phone";
    public const string FooterKey = "Receipts:Footer";

    private static readonly string[] AllKeys =
        [ModeKey, HostKey, PortKey, WindowsPrinterKey, ColumnsKey, CafeNameKey, AddressKey, PhoneKey, FooterKey];

    public async Task<ReceiptSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Settings.AsNoTracking().Where(s => AllKeys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
        var d = ReceiptSettings.Default;

        string Text(string key, string fallback) =>
            rows.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

        var mode = rows.TryGetValue(ModeKey, out var m) && Enum.TryParse<ReceiptMode>(m, true, out var parsedMode)
            && Enum.IsDefined(parsedMode) ? parsedMode : d.Mode;
        var port = rows.TryGetValue(PortKey, out var p)
            && int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort)
            && parsedPort is >= 1 and <= 65535 ? parsedPort : d.Port;
        var columns = rows.TryGetValue(ColumnsKey, out var c)
            && int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedColumns)
            && parsedColumns is 32 or 42 ? parsedColumns : d.Columns;

        return new ReceiptSettings(
            mode, Text(HostKey, string.Empty), port, Text(WindowsPrinterKey, string.Empty), columns,
            Text(CafeNameKey, d.CafeName), Text(AddressKey, string.Empty), Text(PhoneKey, string.Empty), Text(FooterKey, d.Footer));
    }

    public async Task<ServiceResult> SaveAsync(ReceiptSettingsInput input, CancellationToken cancellationToken = default)
    {
        var errors = InputValidation.Validate(input) ?? new Dictionary<string, string>();
        var host = input.Host?.Trim() ?? string.Empty;

        if (!Enum.IsDefined(input.Mode))
        {
            errors[nameof(ReceiptSettingsInput.Mode)] = "Choose a valid print mode.";
        }

        if (input.Columns is not (32 or 42))
        {
            errors[nameof(ReceiptSettingsInput.Columns)] = "Columns must be 32 (58mm paper) or 42 (80mm paper).";
        }

        if (host.Length > 0 && Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            errors.TryAdd(nameof(ReceiptSettingsInput.Host), "Enter a valid printer host name or IP address.");
        }

        if (input.Mode == ReceiptMode.Network && host.Length == 0)
        {
            errors.TryAdd(nameof(ReceiptSettingsInput.Host), "Enter the printer's IP address for network printing.");
        }

        if (errors.Count > 0)
        {
            return ServiceResult.Fail(InputValidation.Summarise(errors), errors);
        }

        var values = new Dictionary<string, string>
        {
            [ModeKey] = input.Mode.ToString(),
            [HostKey] = host,
            [PortKey] = input.Port.ToString(CultureInfo.InvariantCulture),
            [WindowsPrinterKey] = input.WindowsPrinter?.Trim() ?? string.Empty,
            [ColumnsKey] = input.Columns.ToString(CultureInfo.InvariantCulture),
            [CafeNameKey] = input.CafeName?.Trim() ?? string.Empty,
            [AddressKey] = input.Address?.Trim() ?? string.Empty,
            [PhoneKey] = input.Phone?.Trim() ?? string.Empty,
            [FooterKey] = input.Footer?.Trim() ?? string.Empty,
        };

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Settings.Where(s => AllKeys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, cancellationToken);
        foreach (var (key, value) in values)
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

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ServiceResult.Fail("The settings were changed at the same time. Try again.");
        }

        return ServiceResult.Ok();
    }
}
