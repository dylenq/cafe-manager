using System.Net;
using Cafe.Server.Devices;

namespace Cafe.Tests.Infrastructure;

/// <summary>Scripted ITvTransport: no sockets. Records everything the driver does.</summary>
public sealed class FakeTvTransport : ITvTransport
{
    private readonly object _gate = new();

    public List<string> Connects { get; } = [];

    public List<string> Frames { get; } = [];

    public List<(byte[] Payload, IPAddress Target, int Port)> Datagrams { get; } = [];

    public int HttpRequests { get; private set; }

    /// <summary>Reply to each state request. Default: reachable, TV on.</summary>
    public Func<TvHttpResult> Http { get; set; } = () => State("on");

    /// <summary>Frames the next socket will deliver; a null entry means "never answers" (waits the requested timeout, then null).</summary>
    public Queue<string?> Incoming { get; set; } = new();

    public bool ConnectFails { get; set; }

    /// <summary>Local interfaces the driver sees. Empty by default, which makes it use the /24 plus limited broadcast fallback.</summary>
    public List<WakeOnLan.LocalInterface> Interfaces { get; } = [];

    public IReadOnlyList<WakeOnLan.LocalInterface> GetLocalInterfaces() => Interfaces.ToArray();

    /// <summary>Called when a KEY_POWER frame is sent (e.g. to flip the scripted TV state).</summary>
    public Action? OnKeyPower { get; set; }

    public int KeyCount => Frames.Count(f => f.Contains("KEY_POWER"));

    public static TvHttpResult State(string? power) => new(true, 200,
        power is null ? "{\"device\":{\"name\":\"x\"}}" : "{\"device\":{\"PowerState\":\"" + power + "\"}}");

    public static TvHttpResult Down() => new(false, 0, null);

    public static string Connected(string? token = null) =>
        token is null ? "{\"event\":\"ms.channel.connect\",\"data\":{}}" : "{\"event\":\"ms.channel.connect\",\"data\":{\"token\":\"" + token + "\"}}";

    public const string Unauthorized = "{\"event\":\"ms.channel.unauthorized\"}";

    public const string TimeOut = "{\"event\":\"ms.channel.timeOut\"}";

    public Task<ITvSocket?> ConnectAsync(Uri uri, TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Connects.Add(uri.ToString());
        }

        return Task.FromResult<ITvSocket?>(ConnectFails ? null : new Socket(this));
    }

    public Task<TvHttpResult> GetStringAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            HttpRequests++;
            return Task.FromResult(Http());
        }
    }

    public Task SendUdpAsync(byte[] payload, IPAddress target, int port, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Datagrams.Add((payload, target, port));
        }

        return Task.CompletedTask;
    }

    private sealed class Socket(FakeTvTransport owner) : ITvSocket
    {
        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            Action? hook = null;
            lock (owner._gate)
            {
                owner.Frames.Add(text);
                if (text.Contains("KEY_POWER"))
                {
                    hook = owner.OnKeyPower;
                }
            }

            hook?.Invoke();
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveTextAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            string? next;
            bool any;
            lock (owner._gate)
            {
                any = owner.Incoming.TryDequeue(out next);
            }

            if (any && next is not null)
            {
                return next;
            }

            if (any)
            {
                await Task.Delay(timeout, cancellationToken);
            }

            return null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
