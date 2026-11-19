using Cafe.Shared.Agents;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Cafe.Agent.Core;

/// <summary>
/// Owns the SignalR connection to the server and drives the lock screen. Locked before the first connection
/// (AGNT-06 default). Handles Lock and Unlock; the remaining server calls arrive in plan 03-03.
/// </summary>
public sealed class AgentController : IAsyncDisposable
{
    private readonly AgentConnectionOptions _options;
    private readonly ILockScreen _lockScreen;
    private readonly IWarningDisplay _warnings;
    private readonly IPowerControl _power;
    private readonly IAgentClock _clock;
    private readonly ILogger<AgentController> _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private HubConnection? _connection;
    private Task? _connectLoop;
    private volatile AgentState _state = AgentState.Locked;
    private volatile bool _connected;

    public AgentController(
        AgentConnectionOptions options,
        ILockScreen lockScreen,
        IWarningDisplay warnings,
        IPowerControl power,
        IAgentClock clock,
        ILogger<AgentController> logger)
    {
        _options = options;
        _lockScreen = lockScreen;
        _warnings = warnings;
        _power = power;
        _clock = clock;
        _logger = logger;
    }

    public AgentState State => _state;

    public bool IsConnected => _connected;

    public event Action<AgentState>? StateChanged;

    public event Action<bool>? ConnectedChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_connectLoop is not null)
            {
                return Task.CompletedTask;
            }

            // Locked until the server says otherwise.
            ApplyLock(new LockInfo(_options.MachineId, string.Empty, string.Empty));

            var url = _options.ServerUrl.TrimEnd('/') + AgentProtocol.HubPath;
            _connection = new HubConnectionBuilder()
                .WithUrl(url, http =>
                {
                    http.Headers[AgentProtocol.KeyHeader] = _options.SharedKey;
                    http.Headers[AgentProtocol.MachineIdHeader] = _options.MachineId;
                    http.Headers[AgentProtocol.VersionHeader] = _options.AgentVersion;
                    _options.ConfigureHttp?.Invoke(http);
                })
                .Build();

            _connection.On<LockInfo>(nameof(IAgentClient.Lock), ApplyLock);
            _connection.On<SessionInfo>(nameof(IAgentClient.Unlock), ApplyUnlock);
            _connection.Closed += OnClosedAsync;

            _connectLoop = Task.Run(() => ConnectLoopAsync(_stop.Token));
        }

        return Task.CompletedTask;
    }

    private Task OnClosedAsync(Exception? error)
    {
        SetConnected(false);
        if (!_stop.IsCancellationRequested)
        {
            _logger.LogWarning("Connection to the server closed; reconnecting.");
            lock (_gate)
            {
                _connectLoop = Task.Run(() => ConnectLoopAsync(_stop.Token));
            }
        }

        return Task.CompletedTask;
    }

    private async Task ConnectLoopAsync(CancellationToken token)
    {
        var delays = _options.ReconnectDelays is { Count: > 0 } custom ? custom : AgentConnectionOptions.DefaultReconnectDelays;
        for (var attempt = 0; !token.IsCancellationRequested; attempt++)
        {
            var delay = delays[Math.Min(attempt, delays.Count - 1)];
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, token);
                }

                await _connection!.StartAsync(token);
                SetConnected(true);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not reach the server ({Reason}); retrying.", ex.GetType().Name);
            }
        }
    }

    private void SetConnected(bool value)
    {
        if (_connected == value)
        {
            return;
        }

        _connected = value;
        ConnectedChanged?.Invoke(value);
    }

    private void SetState(AgentState state)
    {
        _state = state;
        StateChanged?.Invoke(state);
    }

    private void ApplyLock(LockInfo info)
    {
        _lockScreen.Show(info);
        _warnings.Hide();
        SetState(AgentState.Locked);
    }

    private void ApplyUnlock(SessionInfo info)
    {
        _lockScreen.Hide();
        SetState(AgentState.Unlocked);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_connection is { } connection)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while closing the connection.");
            }
        }

        _stop.Dispose();
    }
}
