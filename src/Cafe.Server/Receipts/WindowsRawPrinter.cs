using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cafe.Server.Receipts;

/// <summary>Writes raw ESC/POS bytes to a Windows printer through winspool. Returns a failure on any other OS.</summary>
public sealed class WindowsRawPrinter : IWindowsReceiptPrinter
{
    public const string NotSupportedMessage = "Windows printing is not supported on this OS.";

    public Task<PrintResult> SendAsync(byte[] data, ReceiptSettings settings, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(PrintResult.Fail(NotSupportedMessage));
        }

        var name = settings.WindowsPrinter?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return Task.FromResult(PrintResult.Fail("No Windows printer name is set in the receipt settings."));
        }

        return Task.Run(() =>
        {
            try
            {
                return OperatingSystem.IsWindows() ? WriteRaw(name, data) : PrintResult.Fail(NotSupportedMessage);
            }
            catch (Exception ex)
            {
                return PrintResult.Fail($"Windows printing failed: {ex.Message}");
            }
        }, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private static PrintResult WriteRaw(string printerName, byte[] data)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
        {
            return PrintResult.Fail($"Could not open Windows printer '{printerName}' (error {Marshal.GetLastWin32Error()}).");
        }

        var buffer = IntPtr.Zero;
        var docStarted = false;
        var pageStarted = false;
        try
        {
            var doc = new DocInfo1 { DocName = "Receipt", OutputFile = null, DataType = "RAW" };
            if (StartDocPrinter(handle, 1, ref doc) == 0)
            {
                return PrintResult.Fail($"Windows printer '{printerName}' refused the job (error {Marshal.GetLastWin32Error()}).");
            }

            docStarted = true;
            if (!StartPagePrinter(handle))
            {
                return PrintResult.Fail($"Windows printer '{printerName}' refused the page (error {Marshal.GetLastWin32Error()}).");
            }

            pageStarted = true;
            buffer = Marshal.AllocCoTaskMem(data.Length);
            Marshal.Copy(data, 0, buffer, data.Length);
            if (!WritePrinter(handle, buffer, data.Length, out var written) || written != data.Length)
            {
                return PrintResult.Fail($"Writing to Windows printer '{printerName}' failed (error {Marshal.GetLastWin32Error()}).");
            }

            return PrintResult.Ok();
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(buffer);
            }

            if (pageStarted)
            {
                EndPagePrinter(handle);
            }

            if (docStarted)
            {
                EndDocPrinter(handle);
            }

            ClosePrinter(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo1
    {
        public string DocName;
        public string? OutputFile;
        public string DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StartDocPrinter(IntPtr handle, int level, ref DocInfo1 docInfo);

    [DllImport("winspool.drv", SetLastError = true, ExactSpelling = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true, ExactSpelling = true)]
    private static extern bool WritePrinter(IntPtr handle, IntPtr bytes, int count, out int written);

    [DllImport("winspool.drv", SetLastError = true, ExactSpelling = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true, ExactSpelling = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true, ExactSpelling = true)]
    private static extern bool ClosePrinter(IntPtr handle);
}
