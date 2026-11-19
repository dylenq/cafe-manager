using System.Media;
using System.Windows.Threading;
using Cafe.Agent.Core;
using Microsoft.Extensions.Logging;

namespace Cafe.Agent;

/// <summary>IWarningDisplay backed by a lazily created WarningToast. Every call is marshalled to the UI dispatcher.</summary>
internal sealed class WpfWarningDisplay : IWarningDisplay
{
    private static readonly TimeSpan MessageDuration = TimeSpan.FromSeconds(10);

    private readonly Dispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _hideTimer;
    private WarningToast? _toast;
    private bool _graceSoundPlayed;

    public WpfWarningDisplay(Dispatcher dispatcher, ILogger logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = MessageDuration };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (_toast is { HasCountdown: false })
            {
                _toast.HideToast();
            }
        };
    }

    public void ShowCountdown(AgentCountdown countdown) => _dispatcher.BeginInvoke(() =>
    {
        Run(() =>
        {
            _hideTimer.Stop();
            if (countdown.IsGrace && !_graceSoundPlayed)
            {
                _graceSoundPlayed = true;
                PlaySound();
            }

            Toast().SetCountdown(countdown);
        });
    });

    public void ShowMessage(string text) => _dispatcher.BeginInvoke(() =>
    {
        Run(() =>
        {
            var toast = Toast();
            toast.SetMessage(text);
            PlaySound();
            _hideTimer.Stop();
            if (!toast.HasCountdown)
            {
                _hideTimer.Start();
            }
        });
    });

    public void Hide() => _dispatcher.BeginInvoke(() =>
    {
        Run(() =>
        {
            _hideTimer.Stop();
            _graceSoundPlayed = false;
            _toast?.HideToast();
        });
    });

    private WarningToast Toast() => _toast ??= new WarningToast();

    private static void PlaySound()
    {
        try
        {
            SystemSounds.Exclamation.Play();
        }
        catch (Exception)
        {
            // Sound is a nicety; never fail the warning because of it.
        }
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Warning display failed: {Type}", ex.GetType().Name);
        }
    }
}
