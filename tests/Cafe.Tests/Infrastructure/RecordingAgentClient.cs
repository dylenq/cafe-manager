using Cafe.Shared.Agents;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cafe.Tests.Infrastructure;

/// <summary>A raw hub client that records every server call in order, for protocol-level assertions.</summary>
public sealed class RecordingAgentClient : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<(string Method, object?[] Args)> _calls = [];
    private readonly HubConnection _connection;
    private bool _closed;

    public RecordingAgentClient(CafeWebApplicationFactory factory, string? key, string? machineId, string version = "test")
    {
        _connection = new HubConnectionBuilder()
            .WithUrl("http://localhost" + AgentProtocol.HubPath, o =>
            {
                if (key is not null)
                {
                    o.Headers[AgentProtocol.KeyHeader] = key;
                }

                if (machineId is not null)
                {
                    o.Headers[AgentProtocol.MachineIdHeader] = machineId;
                }

                o.Headers[AgentProtocol.VersionHeader] = version;
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        _connection.On<LockInfo>("Lock", a => Record("Lock", a));
        _connection.On<SessionInfo>("Unlock", a => Record("Unlock", a));
        _connection.On<SessionInfo>("SessionUpdated", a => Record("SessionUpdated", a));
        _connection.On<int>("ShowWarning", a => Record("ShowWarning", a));
        _connection.On<int>("GraceStarted", a => Record("GraceStarted", a));
        _connection.On<string>("ShowMessage", a => Record("ShowMessage", a));
        _connection.On("Shutdown", () => Record("Shutdown"));
        _connection.On("Restart", () => Record("Restart"));
        _connection.Closed += _ =>
        {
            _closed = true;
            return Task.CompletedTask;
        };
    }

    public bool IsClosed => _closed;

    public IReadOnlyList<(string Method, object?[] Args)> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToList();
            }
        }
    }

    public int Count(string method) => Calls.Count(c => c.Method == method);

    public Task StartAsync() => _connection.StartAsync();

    private void Record(string method, params object?[] args)
    {
        lock (_gate)
        {
            _calls.Add((method, args));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _connection.DisposeAsync();
        }
        catch
        {
            // Test cleanup.
        }
    }
}
