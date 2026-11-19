using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Cafe.Agent.Native;
using Cafe.Shared.Agents;

namespace Cafe.Agent;

/// <summary>
/// Borderless, topmost, maximized lock window for one monitor. Built in code (no XAML). It is not in the taskbar,
/// cannot be closed except through ForceClose, and takes focus back when it loses it while locked.
/// </summary>
internal sealed class LockWindow : Window
{
    private readonly MonitorEnumerator.MonitorRect _monitor;
    private readonly TextBlock _cafe = new();
    private readonly TextBlock _station = new();
    private readonly TextBlock _reason = new();
    private readonly TextBlock _clock = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _allowClose;

    public LockWindow(MonitorEnumerator.MonitorRect monitor, LockInfo info)
    {
        _monitor = monitor;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C));
        Foreground = Brushes.White;
        Title = "Cafe Manager lock";

        _cafe.FontSize = 64;
        _cafe.FontWeight = FontWeights.Bold;
        _station.FontSize = 36;
        _station.Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xB3, 0xC7));
        _reason.FontSize = 40;
        _reason.TextWrapping = TextWrapping.Wrap;
        _reason.TextAlignment = TextAlignment.Center;
        _reason.MaxWidth = 1400;
        _reason.Margin = new Thickness(0, 40, 0, 0);
        _clock.FontSize = 120;
        _clock.FontWeight = FontWeights.Light;
        _clock.Margin = new Thickness(0, 40, 0, 0);

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var block in new[] { _cafe, _station, _reason, _clock })
        {
            block.HorizontalAlignment = HorizontalAlignment.Center;
            block.TextTrimming = TextTrimming.None;
            panel.Children.Add(block);
        }

        Content = new Grid { Children = { panel } };

        Update(info);
        UpdateClock();
        _timer.Tick += (_, _) => UpdateClock();
        _timer.Start();

        SourceInitialized += OnSourceInitialized;
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
    }

    /// <summary>Raised when Ctrl+Alt+Shift+F12 is pressed on this window.</summary>
    public event Action<LockWindow>? EmergencyRequested;

    /// <summary>Set while the emergency dialog is open so it is not fought for focus.</summary>
    public static bool SuppressReactivation { get; set; }

    public void Update(LockInfo info)
    {
        _cafe.Text = info.CafeName;
        _cafe.Visibility = string.IsNullOrWhiteSpace(info.CafeName) ? Visibility.Collapsed : Visibility.Visible;
        _station.Text = info.StationName;
        _station.Visibility = string.IsNullOrWhiteSpace(info.StationName) ? Visibility.Collapsed : Visibility.Visible;
        _reason.Text = info.Reason;
        _reason.Visibility = string.IsNullOrWhiteSpace(info.Reason) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ForceClose()
    {
        _allowClose = true;
        _timer.Stop();
        Close();
    }

    private void UpdateClock() => _clock.Text = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Position in physical pixels so DPI scaling cannot leave gaps at the screen edges.
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            _monitor.Left,
            _monitor.Top,
            _monitor.Width,
            _monitor.Height,
            NativeMethods.SWP_SHOWWINDOW);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_allowClose || AgentApplication.IsExiting || SuppressReactivation)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!_allowClose && !AgentApplication.IsExiting && !SuppressReactivation)
            {
                Topmost = true;
                Activate();
            }
        });
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key == Key.F12 && mods.HasFlag(ModifierKeys.Control) && mods.HasFlag(ModifierKeys.Alt) && mods.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            EmergencyRequested?.Invoke(this);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose && !AgentApplication.IsExiting)
        {
            e.Cancel = true;
        }
    }
}
