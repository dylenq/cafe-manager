using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cafe.Tests.Infrastructure;

public enum FakeTvRemoteMode
{
    /// <summary>Pairing (no token) returns the token; a connection with the right token connects; a wrong token is unauthorized.</summary>
    AcceptWithToken,

    /// <summary>Connects and never returns a token.</summary>
    AcceptNoToken,

    Unauthorized,

    /// <summary>Accepts the socket and says nothing.</summary>
    NeverAnswer,
}

public enum FakeTvRestMode
{
    Normal,
    NoPowerState,
    ServerError,

    /// <summary>Does not answer in time (an unreachable TV).</summary>
    Hang,
}

/// <summary>A minimal in-process Samsung TV: plain ws/http on a random 127.0.0.1 port, remote channel and REST state.</summary>
public sealed class FakeSamsungTv : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly object _gate = new();
    private readonly List<string> _keys = [];
    private string _powerState = "on";

    private FakeSamsungTv(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    public string Token { get; private set; } = "TOKEN123";

    public FakeTvRemoteMode RemoteMode { get; set; } = FakeTvRemoteMode.AcceptWithToken;

    public FakeTvRestMode RestMode { get; set; } = FakeTvRestMode.Normal;

    public string PowerState
    {
        get { lock (_gate) { return _powerState; } }
        set { lock (_gate) { _powerState = value; } }
    }

    public IReadOnlyList<string> Keys
    {
        get { lock (_gate) { return [.. _keys]; } }
    }

    public static async Task<FakeSamsungTv> StartAsync(string token = "TOKEN123")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration["urls"] = "http://127.0.0.1:0";
        var app = builder.Build();
        FakeSamsungTv? tv = null;
        app.UseWebSockets();
        app.MapGet("/api/v2/", (HttpContext http) => tv!.HandleRestAsync(http));
        app.Map("/api/v2/channels/samsung.remote.control", (HttpContext http) => tv!.HandleRemoteAsync(http));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        tv = new FakeSamsungTv(app, new Uri(address).Port) { Token = token };
        return tv;
    }

    private async Task HandleRestAsync(HttpContext http)
    {
        switch (RestMode)
        {
            case FakeTvRestMode.Hang:
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), http.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                }

                return;
            case FakeTvRestMode.ServerError:
                http.Response.StatusCode = 500;
                return;
            case FakeTvRestMode.NoPowerState:
                http.Response.ContentType = "application/json";
                await http.Response.WriteAsync("{\"device\":{\"name\":\"Fake TV\"}}");
                return;
            default:
                // Literal JSON: the minimal-API serializer would camel-case PowerState.
                http.Response.ContentType = "application/json";
                await http.Response.WriteAsync("{\"device\":{\"name\":\"Fake TV\",\"PowerState\":\"" + PowerState + "\"}}");
                return;
        }
    }

    private async Task HandleRemoteAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = 400;
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var presented = http.Request.Query["token"].ToString();
        switch (RemoteMode)
        {
            case FakeTvRemoteMode.Unauthorized:
                await SendAsync(socket, new { @event = "ms.channel.unauthorized" });
                return;
            case FakeTvRemoteMode.NeverAnswer:
                await DrainAsync(socket, http.RequestAborted);
                return;
            case FakeTvRemoteMode.AcceptNoToken:
                await SendAsync(socket, new { @event = "ms.channel.connect", data = new { clients = Array.Empty<object>() } });
                break;
            default:
                if (presented.Length == 0)
                {
                    await SendAsync(socket, new { @event = "ms.channel.connect", data = new { token = Token } });
                }
                else if (presented == Token)
                {
                    await SendAsync(socket, new { @event = "ms.channel.connect", data = new { clients = Array.Empty<object>() } });
                }
                else
                {
                    await SendAsync(socket, new { @event = "ms.channel.unauthorized" });
                    return;
                }

                break;
        }

        await DrainAsync(socket, http.RequestAborted);
    }

    private async Task DrainAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                OnFrame(Encoding.UTF8.GetString(message.ToArray()));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
    }

    private void OnFrame(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("params", out var p) && p.TryGetProperty("DataOfCmd", out var key) && key.GetString() is { } name)
        {
            lock (_gate)
            {
                _keys.Add(name);
                if (name == "KEY_POWER")
                {
                    _powerState = _powerState == "on" ? "standby" : "on";
                }
            }
        }
    }

    private static Task SendAsync(WebSocket socket, object payload) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
