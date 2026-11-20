using System.Net.Sockets;

namespace Cafe.Server.Receipts;

/// <summary>Sends bytes to a thermal printer over raw TCP (usually port 9100) with a connect/write timeout.</summary>
public sealed class NetworkEscPosPrinter(TimeSpan? timeout = null) : INetworkReceiptPrinter
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(5);

    public async Task<PrintResult> SendAsync(byte[] data, ReceiptSettings settings, CancellationToken cancellationToken = default)
    {
        var host = settings.Host?.Trim() ?? string.Empty;
        if (host.Length == 0)
        {
            return PrintResult.Fail("No printer address is set. Enter the printer's IP address in the receipt settings.");
        }

        var unreachable = PrintResult.Fail($"Could not reach the printer at {host}:{settings.Port}.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, settings.Port, timeoutSource.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(data, timeoutSource.Token);
            await stream.FlushAsync(timeoutSource.Token);
            return PrintResult.Ok();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
        {
            return unreachable;
        }
    }
}
