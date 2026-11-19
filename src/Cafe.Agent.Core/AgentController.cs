using Cafe.Shared.Agents;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Cafe.Agent.Core;

/// <summary>
/// Platform-neutral agent: owns the SignalR connection, the state machine (Locked, Unlocked, Grace) and the local
/// one-second countdown. Locked before the first connection and while the server is unreachable. The server is
/// authoritative whenever connected; only a disconnected agent locks itself at zero.
/// </summary>
public sealed class AgentController : IAgentClient, IAsyncDisposable
{
    public const string TimeIsUpReason = "Time is up. Please see the front desk.";

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
    private Task? _tickLoop;
    private bool _disposed;

    // Guarded by _gate.
    private AgentState _state = AgentState.Locked;
    private bool _connected;
    private SessionInfo? _session;
    private TimeSpan _skew;
    private DateTimeOffset _graceEnd;
    private bool _warningActive;
    private long _warningThresholdSeconds;
    private bool _emergency;

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

    public AgentState State
    {
        get { lock (_gate) { return _state; } }
    }

    public bool IsConnected
    {
        get { lock (_gate) { return _connected; } }
    }

    public bool IsEmergencyUnlocked
    {
        get { lock (_gate) { return _emergency; } }
    }

    /// <summary>Seconds left on a prepaid session (null when open-ended or no session).</summary>
    public long? RemainingSeconds
    {
        get { lock (_gate) { return ComputeRemaining(); } }
    }

    /// <summary>Seconds elapsed on the session (counts up for open-ended sessions; 0 with no session).</summary>
    public long ElapsedSeconds
    {
        get { lock (_gate) { return ComputeElapsed(); } }
    }

    /// <summary>The agent clock corrected by the skew measured when the last session info arrived.</summary>
    public DateTimeOffset EstimatedServerNow
    {
        get { lock (_gate) { return _clock.UtcNow + _skew; } }
    }

    public event Action<AgentState>? StateChanged;

    public event Action<bool>? ConnectedChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_connectLoop is not null || _disposed)
            {
                return Task.CompletedTask;
            }

            // Locked until the server says otherwise.
            ApplyLockCore(new LockInfo(_options.MachineId, string.Empty, string.Empty));

            var url = _options.ServerUrl.TrimEnd('/') + AgentProtocol.HubPath;
            _connection = new HubConnectionBuilder()
                .WithUrl(url, http =>
                {
                    http.Headers[AgentProtocol.KeyHeader] = _options.SharedKey;
                    http.Headers[AgentProtocol.MachineIdHeader] = _options.MachineId;
                    http.Headers[AgentProtocol.VersionHeader] = _options.AgentVersion;
                    _options.ConfigureHttp?.Invoke(http);
                })
                .WithAutomaticReconnect(new AgentRetryPolicy(_options.ReconnectDelays))
                .Build();

            _connection.On<LockInfo>(nameof(IAgentClient.Lock), Lock);
            _connection.On<SessionInfo>(nameof(IAgentClient.Unlock), Unlock);
            _connection.On<SessionInfo>(nameof(IAgentClient.SessionUpdated), SessionUpdated);
            _connection.On<int>(nameof(IAgentClient.ShowWarning), ShowWarning);
            _connection.On<int>(nameof(IAgentClient.GraceStarted), GraceStarted);
            _connection.On<string>(nameof(IAgentClient.ShowMessage), ShowMessage);
            _connection.On(nameof(IAgentClient.Shutdown), Shutdown);
            _connection.On(nameof(IAgentClient.Restart), Restart);
            _connection.Reconnecting += _ =>
            {
                HandleDisconnected();
                return Task.CompletedTask;
            };
            _connection.Reconnected += _ =>
            {
                HandleConnected();
                return Task.CompletedTask;
            };
            _connection.Closed += OnClosedAsync;

            _connectLoop = Task.Run(() => ConnectLoopAsync(_stop.Token));
            _tickLoop = Task.Run(() => TickLoopAsync(_stop.Token));
        }

        return Task.CompletedTask;
    }

    // ---- IAgentClient (server to agent) ----

    public Task Lock(LockInfo info)
    {
        lock (_gate)
        {
            ApplyLockCore(info);
        }

        return Task.CompletedTask;
    }

    public Task Unlock(SessionInfo info)
    {
        lock (_gate)
        {
            _emergency = false;
            ClearWarning();
            ClearGrace();
            StoreSession(info);
            Safe(_lockScreen.Hide);
            Safe(_warnings.Hide);
            SetState(AgentState.Unlocked);
        }

        return Task.CompletedTask;
    }

    public Task SessionUpdated(SessionInfo info)
    {
        lock (_gate)
        {
            _emergency = false;
            StoreSession(info);
            if (_warningActive && info.RemainingSeconds is { } remaining && remaining > _warningThresholdSeconds)
            {
                ClearWarning();
                Safe(_warnings.Hide);
            }

            if (_state == AgentState.Locked && !_emergency)
            {
                // Server says there is a session but we were locked: it is authoritative.
                Safe(_lockScreen.Hide);
                SetState(AgentState.Unlocked);
            }
            else if (_state == AgentState.Grace)
            {
                ClearGrace();
                SetState(AgentState.Unlocked);
            }
        }

        return Task.CompletedTask;
    }

    public Task ShowWarning(int minutesRemaining)
    {
        lock (_gate)
        {
            _warningActive = true;
            _warningThresholdSeconds = minutesRemaining * 60L;
            var text = minutesRemaining == 1 ? "1 minute remaining" : $"{minutesRemaining} minutes remaining";
            Safe(() => _warnings.ShowMessage(text));
            ShowWarningCountdown();
        }

        return Task.CompletedTask;
    }

    public Task GraceStarted(int graceSeconds)
    {
        lock (_gate)
        {
            _emergency = false;
            _graceEnd = _clock.UtcNow.AddSeconds(graceSeconds);
            _warningActive = false;
            Safe(_lockScreen.Hide);
            SetState(AgentState.Grace);
            ShowGraceCountdown();
        }

        return Task.CompletedTask;
    }

    public Task ShowMessage(string text)
    {
        Safe(() => _warnings.ShowMessage(text));
        return Task.CompletedTask;
    }

    public Task Shutdown()
    {
        Safe(_power.Shutdown);
        return Task.CompletedTask;
    }

    public Task Restart()
    {
        Safe(_power.Restart);
        return Task.CompletedTask;
    }

    // ---- local operations ----

    /// <summary>Unlocks locally with no countdown until the next server push.</summary>
    public void EmergencyUnlock()
    {
        lock (_gate)
        {
            _emergency = true;
            _session = null;
            ClearWarning();
            ClearGrace();
            Safe(_lockScreen.Hide);
            Safe(_warnings.Hide);
            SetState(AgentState.Unlocked);
        }
    }

    /// <summary>Advances the local countdown. Called once a second by the internal loop; tests call it directly.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            switch (_state)
            {
                case AgentState.Unlocked:
                    if (_warningActive)
                    {
                        ShowWarningCountdown();
                    }

                    if (!_connected && ComputeRemaining() is 0)
                    {
                        ApplyLockCore(new LockInfo(_session?.StationName ?? _options.MachineId, string.Empty, TimeIsUpReason));
                    }

                    break;
                case AgentState.Grace:
                    ShowGraceCountdown();
                    if (!_connected && GraceSecondsLeft() <= 0)
                    {
                        ApplyLockCore(new LockInfo(_session?.StationName ?? _options.MachineId, string.Empty, TimeIsUpReason));
                    }

                    break;
            }
        }
    }

    internal void HandleConnected()
    {
        bool changed;
        lock (_gate)
        {
            changed = !_connected;
            _connected = true;
        }

        if (changed)
        {
            Safe(() => ConnectedChanged?.Invoke(true));
        }
    }

    internal void HandleDisconnected()
    {
        bool changed;
        lock (_gate)
        {
            changed = _connected;
            _connected = false;
        }

        if (changed)
        {
            Safe(() => ConnectedChanged?.Invoke(false));
        }

        Tick();
    }

    // ---- internals (call under _gate) ----

    private void ApplyLockCore(LockInfo info)
    {
        _emergency = false;
        _session = null;
        ClearWarning();
        ClearGrace();
        Safe(() => _lockScreen.Show(info));
        Safe(_warnings.Hide);
        SetState(AgentState.Locked);
    }

    private void StoreSession(SessionInfo info)
    {
        _session = info;
        _skew = info.ServerUtcNow - _clock.UtcNow;
    }

    private void ClearWarning()
    {
        _warningActive = false;
        _warningThresholdSeconds = 0;
    }

    private void ClearGrace() => _graceEnd = default;

    private long SecondsSinceReceipt()
    {
        if (_session is null)
        {
            return 0;
        }

        var delta = (_clock.UtcNow + _skew) - _session.ServerUtcNow;
        return (long)Math.Floor(delta.TotalSeconds);
    }

    private long? ComputeRemaining()
    {
        if (_session?.RemainingSeconds is not { } remaining)
        {
            return null;
        }

        return Math.Max(0, remaining - SecondsSinceReceipt());
    }

    private long ComputeElapsed() => _session is null ? 0 : _session.ElapsedSeconds + Math.Max(0, SecondsSinceReceipt());

    private long GraceSecondsLeft() =>
        (long)Math.Max(0, Math.Ceiling((_graceEnd - _clock.UtcNow).TotalSeconds));

    private void ShowWarningCountdown()
    {
        if (ComputeRemaining() is { } remaining)
        {
            Safe(() => _warnings.ShowCountdown(new AgentCountdown(remaining, false, false)));
        }
    }

    private void ShowGraceCountdown()
    {
        var left = GraceSecondsLeft();
        Safe(() => _warnings.ShowCountdown(new AgentCountdown(left, true, false)));
    }

    private void SetState(AgentState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        Safe(() => StateChanged?.Invoke(state));
    }

    private void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("A display or handler failed ({Reason}); continuing.", ex.GetType().Name);
        }
    }

    // ---- connection ----

    private Task OnClosedAsync(Exception? error)
    {
        HandleDisconnected();
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
        for (var attempt = 0; !token.IsCancellationRequested; attempt++)
        {
            try
            {
                var delay = AgentRetryPolicy.DelayFor(attempt, _options.ReconnectDelays);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, token);
                }

                await _connection!.StartAsync(token);
                HandleConnected();
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

    private async Task TickLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                Tick();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

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

        foreach (var loop in new[] { _connectLoop, _tickLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Best effort during shutdown.
            }
        }

        _stop.Dispose();
    }
}
