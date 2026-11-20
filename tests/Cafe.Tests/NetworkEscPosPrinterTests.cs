using System.Net;
using System.Net.Sockets;
using Cafe.Server.Receipts;

namespace Cafe.Tests;

public class NetworkEscPosPrinterTests
{
    private static ReceiptSettings Settings(string host, int port, string windows = "") =>
        ReceiptSettings.Default with { Mode = ReceiptMode.Network, Host = host, Port = port, WindowsPrinter = windows };

    /// <summary>Accepts one connection and reads it to the end.</summary>
    internal static (TcpListener Listener, Task<byte[]> Received) Capture()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var received = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var ms = new MemoryStream();
            await client.GetStream().CopyToAsync(ms);
            return ms.ToArray();
        });
        return (listener, received);
    }

    [Fact]
    public async Task Sends_exactly_the_bytes_over_tcp()
    {
        var (listener, received) = Capture();
        try
        {
            var payload = Enumerable.Range(0, 300).Select(i => (byte)(i % 251)).ToArray();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var result = await new NetworkEscPosPrinter().SendAsync(payload, Settings("127.0.0.1", port));

            Assert.True(result.Success, result.Error);
            Assert.Equal(payload, await received.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Connection_refused_returns_a_failed_result_without_throwing()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await new NetworkEscPosPrinter(TimeSpan.FromSeconds(2)).SendAsync([1, 2, 3], Settings("127.0.0.1", port));

        Assert.False(result.Success);
        Assert.Contains("Could not reach the printer", result.Error);
    }

    [Fact]
    public async Task A_listener_that_never_reads_cannot_hang_the_printer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = listener.AcceptTcpClientAsync();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var huge = new byte[64 * 1024 * 1024];

            var started = DateTime.UtcNow;
            var result = await new NetworkEscPosPrinter(TimeSpan.FromMilliseconds(400)).SendAsync(huge, Settings("127.0.0.1", port));

            Assert.False(result.Success);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Stop();
            (await accepted.ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null))?.Dispose();
        }
    }

    [Fact]
    public async Task Empty_host_fails_fast()
    {
        var result = await new NetworkEscPosPrinter().SendAsync([1], Settings("  ", 9100));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Windows_printer_is_not_supported_off_windows_and_never_throws()
    {
        var result = await new WindowsRawPrinter().SendAsync([1], Settings("", 9100, "Receipt"));

        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(result);
        }
        else
        {
            Assert.False(result.Success);
            Assert.Contains("not supported on this OS", result.Error);
        }

        var blank = await new WindowsRawPrinter().SendAsync([1], Settings("", 9100, " "));
        Assert.False(blank.Success);
    }
}
