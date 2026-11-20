using System.Net;

namespace Cafe.Server.Devices;

/// <summary>Everything the driver needs from the network, so tests can script it.</summary>
public interface ITvTransport
{
    /// <summary>Opens the remote channel. Returns null when the TV cannot be reached in time; never throws except on cancellation.</summary>
    Task<ITvSocket?> ConnectAsync(Uri uri, TimeSpan connectTimeout, CancellationToken cancellationToken);

    Task<TvHttpResult> GetStringAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken);

    Task SendUdpAsync(byte[] payload, IPAddress target, int port, CancellationToken cancellationToken);

    /// <summary>Up IPv4 interfaces of this machine, used to pick the Wake-on-LAN broadcast addresses. Empty when unknown.</summary>
    IReadOnlyList<WakeOnLan.LocalInterface> GetLocalInterfaces();
}

public interface ITvSocket : IAsyncDisposable
{
    Task SendTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>Next complete text message, or null on timeout or when the TV closed the channel.</summary>
    Task<string?> ReceiveTextAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Reached = false means timeout or connection failure.</summary>
public sealed record TvHttpResult(bool Reached, int StatusCode, string? Body);
