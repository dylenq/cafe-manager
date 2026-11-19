using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Cafe.Agent.Core;
using Cafe.Agent.Native;

namespace Cafe.Agent;

/// <summary>
/// Small topmost window at the bottom right of the primary screen. It never takes focus (WS_EX_NOACTIVATE,
/// ShowActivated false, Focusable false) and is hidden from Alt+Tab (WS_EX_TOOLWINDOW), so a running game is not
/// disturbed. All text is set through TextBlock.Text, so message text is plain text and never parsed as markup.
/// </summary>
internal sealed class WarningToast : Window
{
    public const string GraceText = "Time is up — save your work";

    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0x6A, 0x00));
    private static readonly Brush GraceBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0x1E, 0x1E));

    private readonly Border _frame = new() { Padding = new Thickness(20, 14, 20, 14), CornerRadius = new CornerRadius(8), Background = WarningBrush };
    private readonly TextBlock _countdown = new() { FontSize = 40, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _message = new() { FontSize = 18, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };

    public WarningToast()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Cafe Manager warning";

        var panel = new StackPanel();
        panel.Children.Add(_countdown);
        panel.Children.Add(_message);
        _frame.Child = panel;
        Content = _frame;

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Reposition();
    }

    public void SetCountdown(AgentCountdown countdown)
    {
        _countdown.Text = Format(countdown.RemainingSeconds);
        _countdown.Visibility = Visibility.Visible;
        if (countdown.IsGrace)
        {
            _frame.Background = GraceBrush;
            _message.Text = GraceText;
        }
        else
        {
            _frame.Background = WarningBrush;
        }

        EnsureShown();
    }

    public void SetMessage(string text)
    {
        _message.Text = text;
        EnsureShown();
    }

    public void HideToast()
    {
        _frame.Background = WarningBrush;
        _countdown.Text = string.Empty;
        _message.Text = string.Empty;
        if (IsVisible)
        {
            Hide();
        }
    }

    public bool HasCountdown => _countdown.Text.Length > 0;

    public void ClearCountdown() => _countdown.Text = string.Empty;

    private void EnsureShown()
    {
        _countdown.Visibility = _countdown.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!IsVisible)
        {
            Show();
        }

        Reposition();
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, style | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
    }

    private static string Format(long seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes:00}:{t.Seconds:00}");
    }
}
