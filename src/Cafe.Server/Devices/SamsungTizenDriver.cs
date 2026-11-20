using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cafe.Server.Data;
using Microsoft.Extensions.Options;

namespace Cafe.Server.Devices;

/// <summary>
/// Samsung Tizen driver: pairing and power key over the remote WebSocket channel, power state over the REST
/// endpoint. KEY_POWER toggles, so a key is only ever sent after the state says the TV is On (or unknown), and
/// actions on one TV never overlap. The pairing token is never logged.
/// </summary>
public sealed class SamsungTizenDriver(ITvTransport transport, IOptions<TvOptions> options, ILogger<SamsungTizenDriver> logger) : ITvDriver
{
    /// <summary>Stored when a TV pairs without returning a token. Non-blank on purpose: blank strings are cleaned to null.</summary>
    public const string NoTokenMarker = "-";

    private const string RemoteName = "CafeManager";

    private static readonly string PowerKeyFrame = JsonSerializer.Serialize(new
    {
        method = "ms.remote.control",
        @params = new { Cmd = "Click", DataOfCmd = "KEY_POWER", Option = "false", TypeOfRemote = "SendRemoteKey" },
    });

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    private TvOptions Options => options.Value;

    public async Task<TvPowerState> GetStateAsync(Station station, CancellationToken cancellationToken)
    {
        if (!TryGetIp(station, out var ip))
        {
            return TvPowerState.Unknown;
        }

        return await ReadStateAsync(ip, cancellationToken);
    }

    public async Task<TvPairResult> PairAsync(Station station, CancellationToken cancellationToken)
    {
        if (!TryGetIp(station, out var ip))
        {
            return new TvPairResult(false, null, "This station has no valid TV IP address");
        }

        using var gate = await LockAsync(ip, cancellationToken);
        try
        {
            var timeout = TimeSpan.FromSeconds(Options.PairTimeoutSeconds);
            await using var socket = await transport.ConnectAsync(RemoteUri(ip, null), TimeSpan.FromSeconds(Options.ConnectTimeoutSeconds), cancellationToken);
            if (socket is null)
            {
                return new TvPairResult(false, null, "Could not connect to the TV. Check the IP address and that the TV is on");
            }

            var wait = await WaitForChannelAsync(socket, timeout, cancellationToken);
            return wait.Kind switch
            {
                ChannelWait.Connected => new TvPairResult(true, string.IsNullOrEmpty(wait.Token) ? NoTokenMarker : wait.Token, null),
                ChannelWait.Unauthorized => new TvPairResult(false, null, "The TV refused the pairing request"),
                ChannelWait.TimedOutEvent => new TvPairResult(false, null, "The TV timed out waiting for the prompt. Accept the prompt on the TV and try again"),
                ChannelWait.TimedOut => new TvPairResult(false, null, "The TV did not respond in time. Accept the prompt on the TV and try again"),
                _ => new TvPairResult(false, null, "The TV closed the connection before pairing finished"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Pairing with the TV of station {Station} failed.", station.Name);
            return new TvPairResult(false, null, "Pairing failed: the TV sent an unexpected reply");
        }
    }

    public async Task<TvActionResult> PowerOffAsync(Station station, CancellationToken cancellationToken)
    {
        if (!TryGetIp(station, out var ip))
        {
            return new TvActionResult(TvActionOutcome.Failed, TvPowerState.Unknown, "This station has no valid TV IP address");
        }

        if (string.IsNullOrWhiteSpace(station.TvToken))
        {
            // Connecting unpaired would pop a prompt on the TV that nobody accepts.
            return new TvActionResult(TvActionOutcome.Failed, TvPowerState.Unknown, "The TV is not paired");
        }

        using var gate = await LockAsync(ip, cancellationToken);
        try
        {
            var state = await ReadStateAsync(ip, cancellationToken);
            switch (state)
            {
                case TvPowerState.Standby:
                    return new TvActionResult(TvActionOutcome.AlreadyOff, state, "The TV is already off");
                case TvPowerState.Unreachable:
                    return new TvActionResult(TvActionOutcome.Unreachable, state, "The TV did not respond");
            }

            if (await SendPowerKeyAsync(ip, station.TvToken, cancellationToken) is { } failure)
            {
                return failure;
            }

            await DelayAsync(Options.SettleMilliseconds, cancellationToken);
            var after = await ReadStateAsync(ip, cancellationToken);
            return after switch
            {
                TvPowerState.Standby or TvPowerState.Unreachable => new TvActionResult(TvActionOutcome.PoweredOff, after, "The TV was switched off"),
                TvPowerState.On => new TvActionResult(TvActionOutcome.Failed, after, "The TV is still on after the power key"),
                _ => new TvActionResult(TvActionOutcome.KeySent, after, "The power key was sent, the TV state is unknown"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Powering off the TV of station {Station} failed.", station.Name);
            return new TvActionResult(TvActionOutcome.Failed, TvPowerState.Unknown, "Powering off failed: " + ex.GetType().Name);
        }
    }

    public Task<TvActionResult> PowerOnAsync(Station station, CancellationToken cancellationToken) =>
        Task.FromResult(new TvActionResult(TvActionOutcome.Failed, TvPowerState.Unknown, "Power on is completed in Task 2 of this plan"));

    /// <summary>Sends KEY_POWER over a fresh channel. Returns null when sent, else the failure to report.</summary>
    private async Task<TvActionResult?> SendPowerKeyAsync(string ip, string token, CancellationToken ct)
    {
        await using var socket = await transport.ConnectAsync(RemoteUri(ip, token), TimeSpan.FromSeconds(Options.ConnectTimeoutSeconds), ct);
        if (socket is null)
        {
            return new TvActionResult(TvActionOutcome.Failed, TvPowerState.Unknown, "Could not open the remote channel to the TV");
        }

        var wait = await WaitForChannelAsync(socket, TimeSpan.FromSeconds(Options.ConnectTimeoutSeconds), ct);
        if (wait.Kind == ChannelWait.Unauthorized)
        {
            return new TvActionResult(TvActionOutcome.Failed, TvPowerState.Unknown, "The TV rejected the stored pairing, pair it again");
        }

        if (wait.Kind != ChannelWait.Connected)
        {
            return new TvActionResult(TvActionOutcome.Failed, TvPowerState.Unknown, "The TV did not accept the remote channel");
        }

        await socket.SendTextAsync(PowerKeyFrame, ct);
        await DelayAsync(Options.KeyFlushMilliseconds, ct);
        return null;
    }

    private async Task<TvPowerState> ReadStateAsync(string ip, CancellationToken ct)
    {
        var uri = new UriBuilder(Uri.UriSchemeHttp, ip, Options.RestPort, "/api/v2/").Uri;
        var reply = await transport.GetStringAsync(uri, TimeSpan.FromSeconds(Options.RestTimeoutSeconds), ct);
        if (!reply.Reached)
        {
            return TvPowerState.Unreachable;
        }

        if (reply.StatusCode is < 200 or > 299 || reply.Body is null)
        {
            return TvPowerState.Unknown;
        }

        try
        {
            using var doc = JsonDocument.Parse(reply.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return TvPowerState.Unknown;
            }

            if (doc.RootElement.TryGetProperty("device", out var device) && device.ValueKind == JsonValueKind.Object
                && device.TryGetProperty("PowerState", out var power))
            {
                return power.ValueKind == JsonValueKind.String
                    ? power.GetString()?.ToLowerInvariant() switch
                    {
                        "on" => TvPowerState.On,
                        "standby" => TvPowerState.Standby,
                        _ => TvPowerState.Unknown,
                    }
                    : TvPowerState.Unknown;
            }

            // Older models answer only while on and do not report PowerState.
            return TvPowerState.On;
        }
        catch (JsonException)
        {
            return TvPowerState.Unknown;
        }
    }

    private enum ChannelWait
    {
        Connected,
        Unauthorized,
        TimedOutEvent,
        TimedOut,
        Closed,
    }

    private readonly record struct ChannelResult(ChannelWait Kind, string? Token);

    /// <summary>Reads channel events until connect, unauthorized, timeOut, close or the overall timeout.</summary>
    private static async Task<ChannelResult> WaitForChannelAsync(ITvSocket socket, TimeSpan timeout, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return new ChannelResult(ChannelWait.TimedOut, null);
            }

            var text = await socket.ReceiveTextAsync(remaining, ct);
            if (text is null)
            {
                return clock.Elapsed >= timeout - TimeSpan.FromMilliseconds(50)
                    ? new ChannelResult(ChannelWait.TimedOut, null)
                    : new ChannelResult(ChannelWait.Closed, null);
            }

            string? name;
            string? token = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                name = doc.RootElement.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("token", out var t))
                {
                    token = t.ValueKind == JsonValueKind.String ? t.GetString() : t.ToString();
                }
            }
            catch (JsonException)
            {
                continue;
            }

            switch (name)
            {
                case "ms.channel.connect":
                    return new ChannelResult(ChannelWait.Connected, token);
                case "ms.channel.unauthorized":
                    return new ChannelResult(ChannelWait.Unauthorized, null);
                case "ms.channel.timeOut":
                    return new ChannelResult(ChannelWait.TimedOutEvent, null);
            }
        }
    }

    private Uri RemoteUri(string ip, string? token)
    {
        var query = new StringBuilder("name=").Append(Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(RemoteName))));
        if (!string.IsNullOrWhiteSpace(token) && token != NoTokenMarker)
        {
            query.Append("&token=").Append(Uri.EscapeDataString(token));
        }

        return new UriBuilder(Options.UseTls ? "wss" : "ws", ip, Options.RemotePort, "/api/v2/channels/samsung.remote.control")
        {
            Query = query.ToString(),
        }.Uri;
    }

    private static bool TryGetIp(Station station, out string ip)
    {
        ip = string.Empty;
        var text = station.TvIpAddress?.Trim();
        if (string.IsNullOrEmpty(text) || text.Split('.').Length != 4
            || !IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        ip = address.ToString();
        return true;
    }

    private static Task DelayAsync(int milliseconds, CancellationToken ct) =>
        milliseconds > 0 ? Task.Delay(milliseconds, ct) : Task.CompletedTask;

    private async Task<IDisposable> LockAsync(string ip, CancellationToken ct)
    {
        var semaphore = _locks.GetOrAdd(ip, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
