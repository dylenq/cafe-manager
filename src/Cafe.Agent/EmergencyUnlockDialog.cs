using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cafe.Agent.Core;

namespace Cafe.Agent;

/// <summary>
/// Modal prompt for the emergency unlock code. The entered code is never echoed or logged; it is verified with
/// EmergencyCodeHasher against the configured hash. 3 wrong codes impose an escalating lockout that survives an agent restart (see EmergencyAttemptTracker).
/// </summary>
internal sealed class EmergencyUnlockDialog : Window
{
    private readonly string _storedHash;
    private readonly EmergencyAttemptTracker _attempts;
    private readonly PasswordBox _code = new() { FontSize = 24, MinWidth = 280, Margin = new Thickness(0, 12, 0, 8) };
    private readonly CheckBox _alsoExit = new() { Content = "Also exit the agent", Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button _ok = new() { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 80 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Firebrick, MaxWidth = 320 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public EmergencyUnlockDialog(string storedHash, EmergencyAttemptTracker attempts)
    {
        _storedHash = storedHash;
        _attempts = attempts;

        Title = "Emergency unlock";
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(20) };
        if (string.IsNullOrWhiteSpace(storedHash))
        {
            panel.Children.Add(new TextBlock { Text = "Emergency unlock is not configured on this PC.", TextWrapping = TextWrapping.Wrap, MaxWidth = 320 });
            _cancel.Content = "Close";
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 16, 0, 0),
                Children = { _cancel },
            });
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = "Enter the emergency unlock code" });
            panel.Children.Add(_code);
            panel.Children.Add(_alsoExit);
            panel.Children.Add(_status);
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 12, 0, 0),
                Children = { _ok, _cancel },
            });
        }

        Content = panel;
        _ok.Click += OnOk;
        _timer.Tick += (_, _) => RefreshLockout();
        Loaded += (_, _) =>
        {
            RefreshLockout();
            _code.Focus();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _code.Clear();
        };
    }

    public bool Unlocked { get; private set; }

    public bool ExitAgent { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_attempts.RemainingLockout > TimeSpan.Zero)
        {
            RefreshLockout();
            return;
        }

        var ok = EmergencyCodeHasher.Verify(_code.Password, _storedHash);
        _code.Clear();
        if (ok)
        {
            _attempts.Reset();
            Unlocked = true;
            ExitAgent = _alsoExit.IsChecked == true;
            DialogResult = true;
            return;
        }

        _attempts.RecordFailure();
        _status.Text = "Wrong code.";
        RefreshLockout();
        _code.Focus();
    }

    private void RefreshLockout()
    {
        var left = _attempts.RemainingLockout;
        if (left > TimeSpan.Zero)
        {
            _ok.IsEnabled = false;
            _code.IsEnabled = false;
            _status.Text = $"Too many wrong codes. Try again in {(int)Math.Ceiling(left.TotalSeconds)} s.";
            _timer.Start();
        }
        else
        {
            _ok.IsEnabled = true;
            _code.IsEnabled = true;
            _timer.Stop();
        }
    }
}
