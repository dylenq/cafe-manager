using System.Windows;
using System.Windows.Threading;
using Cafe.Agent.Core;
using Cafe.Agent.Native;
using Cafe.Shared.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Cafe.Agent;

/// <summary>
/// ILockScreen backed by one LockWindow per monitor. AgentController calls from SignalR threads, so every call is
/// marshalled to the UI dispatcher. The keyboard hook is installed only while locked so games are never affected.
/// </summary>
internal sealed class WpfLockScreen : ILockScreen
{
    private readonly Dispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly string _emergencyHash;
    private readonly Action<bool> _onEmergencyUnlock;
    private readonly KeyboardBlocker _blocker = new();
    private readonly EmergencyAttemptTracker _attempts = new();
    private readonly List<LockWindow> _windows = [];
    private LockInfo? _info;
    private bool _subscribed;
    private bool _dialogOpen;

    public WpfLockScreen(Dispatcher dispatcher, ILogger logger, string emergencyHash, Action<bool> onEmergencyUnlock)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _emergencyHash = emergencyHash;
        _onEmergencyUnlock = onEmergencyUnlock;
    }

    public void Show(LockInfo info) => _dispatcher.BeginInvoke(() => ShowCore(info));

    public void Hide() => _dispatcher.BeginInvoke(HideCore);

    private void ShowCore(LockInfo info)
    {
        try
        {
            _info = info;
            if (_windows.Count > 0)
            {
                foreach (var window in _windows)
                {
                    window.Update(info);
                }

                return;
            }

            BuildWindows();
            _blocker.Install();
            if (!_subscribed)
            {
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                _subscribed = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Could not show the lock screen: {Type}: {Message}", ex.GetType().Name, ex.Message);
        }
    }

    private void HideCore()
    {
        try
        {
            _info = null;
            _blocker.Uninstall();
            if (_subscribed)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _subscribed = false;
            }

            CloseWindows();
        }
        catch (Exception ex)
        {
            _logger.LogError("Could not hide the lock screen: {Type}: {Message}", ex.GetType().Name, ex.Message);
        }
    }

    private void BuildWindows()
    {
        var info = _info!;
        var monitors = MonitorEnumerator.GetMonitorRects();
        if (monitors.Count == 0)
        {
            // Enumeration failed: still cover the primary screen.
            monitors = [new MonitorEnumerator.MonitorRect(0, 0, (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight)];
        }

        foreach (var monitor in monitors)
        {
            var window = new LockWindow(monitor, info);
            window.EmergencyRequested += OnEmergencyRequested;
            _windows.Add(window);
            window.Show();
        }

        _windows[0].Activate();
    }

    private void CloseWindows()
    {
        foreach (var window in _windows)
        {
            window.EmergencyRequested -= OnEmergencyRequested;
            window.ForceClose();
        }

        _windows.Clear();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Raised on a system thread: rebuild on the UI thread so every screen stays covered.
        _dispatcher.BeginInvoke(() =>
        {
            if (_info is null || _windows.Count == 0)
            {
                return;
            }

            CloseWindows();
            BuildWindows();
        });
    }

    private void OnEmergencyRequested(LockWindow source)
    {
        if (_dialogOpen)
        {
            return;
        }

        _dialogOpen = true;
        LockWindow.SuppressReactivation = true;
        try
        {
            var dialog = new EmergencyUnlockDialog(_emergencyHash, _attempts) { Owner = source };
            dialog.ShowDialog();
            if (dialog.Unlocked)
            {
                _logger.LogWarning("Emergency unlock accepted.");
                _onEmergencyUnlock(dialog.ExitAgent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Emergency unlock dialog failed: {Type}", ex.GetType().Name);
        }
        finally
        {
            LockWindow.SuppressReactivation = false;
            _dialogOpen = false;
            if (_windows.Count > 0)
            {
                _windows[0].Activate();
            }
        }
    }
}
