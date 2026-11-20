using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace Cafe.Server.Devices;

/// <summary>
/// Real network transport. The TV serves its remote channel with a self-signed certificate, so the certificate
/// callback below accepts any certificate. It is set only on the ClientWebSocket created here, for a TV address
/// configured by the admin, and nowhere else (no process-wide or HttpClient-level override). The URI carries the
/// pairing token and is never logged.
/// </summary>
public sealed class SamsungTvTransport : ITvTransport
{
    private const int MaxMessageBytes = 64 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false });

    public async Task<ITvSocket?> ConnectAsync(Uri uri, TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(connectTimeout);
        try
        {
            await socket.ConnectAsync(uri, timeout.Token);
            return new Socket(socket);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or HttpRequestException or SocketException or IOException)
        {
            socket.Dispose();
            return null;
        }
    }

    public async Task<TvHttpResult> GetStringAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var body = await ReadLimitedAsync(response.Content, cts.Token);
            return new TvHttpResult(true, (int)response.StatusCode, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or SocketException or IOException)
        {
            return new TvHttpResult(false, 0, null);
        }
    }

    public async Task SendUdpAsync(byte[] payload, IPAddress target, int port, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        await udp.SendAsync(payload, new IPEndPoint(target, port), cancellationToken);
    }

    public IReadOnlyList<WakeOnLan.LocalInterface> GetLocalInterfaces()
    {
        var result = new List<WakeOnLan.LocalInterface>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && unicast.IPv4Mask is { } mask)
                    {
                        result.Add(new WakeOnLan.LocalInterface(unicast.Address, mask));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            // Fall back to the /24 guess plus the limited broadcast.
        }

        return result;
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxMessageBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private sealed class Socket(ClientWebSocket socket) : ITvSocket
    {
        public Task SendTextAsync(string text, CancellationToken cancellationToken) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);

        public async Task<string?> ReceiveTextAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            var buffer = new byte[4096];
            using var message = new MemoryStream();
            try
            {
                while (true)
                {
                    if (socket.State is not (WebSocketState.Open or WebSocketState.CloseSent))
                    {
                        return null;
                    }

                    var result = await socket.ReceiveAsync(buffer, cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return null;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxMessageBytes)
                    {
                        return null;
                    }

                    if (result.EndOfMessage)
                    {
                        return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
            {
            }
            finally
            {
                socket.Dispose();
            }
        }
    }
}
