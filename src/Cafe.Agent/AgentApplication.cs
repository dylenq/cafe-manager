using System.IO;
using System.Reflection;
using System.Windows;
using Cafe.Agent.Core;
using Cafe.Shared.Agents;
using Microsoft.Extensions.Logging;

namespace Cafe.Agent;

/// <summary>
/// The WPF application: loads agentsettings.json, builds the real Windows implementations and starts the
/// platform-neutral AgentController. Fails secure: a bad configuration keeps the PC on the lock screen.
/// The SessionEnding event is deliberately never handled or cancelled, so Windows shutdown, restart and logoff
/// are never blocked by the agent.
/// </summary>
internal sealed class AgentApplication : Application
{
    private ILoggerFactory? _loggerFactory;
    private ILogger<AgentApplication>? _logger;
    private AgentController? _controller;
    private ILockScreen? _lockScreen;
    private IWarningDisplay? _warnings;
    private bool _exiting;

    public AgentApplication()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    /// <summary>True once ExitAgent() was requested; lock windows only allow closing then.</summary>
    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(Path.Combine(AppContext.BaseDirectory, "logs")));
        });
        _logger = _loggerFactory.CreateLogger<AgentApplication>();

        DispatcherUnhandledException += (_, args) =>
        {
            _logger.LogError("Unhandled UI error: {Type}: {Message}", args.Exception.GetType().Name, args.Exception.Message);
            args.Handled = true;
        };

        var settingsPath = Path.Combine(AppContext.BaseDirectory, "agentsettings.json");
        var load = AgentSettingsLoader.Load(settingsPath, Environment.MachineName);

        _warnings = CreateWarningDisplay();
        _lockScreen = CreateLockScreen(load.Settings);

        if (!load.Success)
        {
            // Fail secure: never start the controller, keep the PC locked and say why.
            foreach (var error in load.Errors)
            {
                _logger.LogError("Configuration error: {Error}", error);
            }

            var reason = "Agent configuration error: " + string.Join(" ", load.Errors);
            _lockScreen.Show(new LockInfo(Environment.MachineName, string.Empty, reason));
            return;
        }

        var settings = load.Settings!;
        var options = new AgentConnectionOptions
        {
            ServerUrl = settings.ServerUrl,
            SharedKey = settings.SharedKey,
            MachineId = settings.MachineId,
            AgentVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0",
        };

        _controller = new AgentController(
            options,
            _lockScreen,
            _warnings,
            new PowerControl(_loggerFactory.CreateLogger<PowerControl>()),
            new SystemAgentClock(),
            _loggerFactory.CreateLogger<AgentController>());

        _logger.LogInformation("Agent {MachineId} starting.", settings.MachineId);
        _ = _controller.StartAsync();
    }

    /// <summary>Overridden wiring point for the lock screen (real WPF implementation is supplied by the factory below).</summary>
    internal Func<AgentApplication, AgentSettings?, ILogger, ILockScreen> LockScreenFactory { get; set; } =
        static (_, _, _) => new NullLockScreen();

    internal Func<AgentApplication, ILogger, IWarningDisplay> WarningDisplayFactory { get; set; } =
        static (_, _) => new NullWarningDisplay();

    private ILockScreen CreateLockScreen(AgentSettings? settings) =>
        LockScreenFactory(this, settings, _loggerFactory!.CreateLogger("LockScreen"));

    private IWarningDisplay CreateWarningDisplay() =>
        WarningDisplayFactory(this, _loggerFactory!.CreateLogger("WarningDisplay"));

    /// <summary>Called by the lock screen after a successful emergency unlock.</summary>
    internal void OnEmergencyUnlock(bool exitAgent)
    {
        _logger?.LogWarning("Emergency unlock used (exit agent: {Exit}).", exitAgent);
        if (_controller is not null)
        {
            _controller.EmergencyUnlock();
        }
        else
        {
            _lockScreen?.Hide();
        }

        if (exitAgent)
        {
            ExitAgent();
        }
    }

    /// <summary>The only way the agent exits: reached from the emergency unlock path.</summary>
    internal void ExitAgent()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        IsExiting = true;
        try
        {
            _controller?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Error while stopping the agent: {Reason}", ex.GetType().Name);
        }

        _lockScreen?.Hide();
        _warnings?.Hide();
        _loggerFactory?.Dispose();
        Shutdown();
    }

    private sealed class NullLockScreen : ILockScreen
    {
        public void Show(LockInfo info)
        {
        }

        public void Hide()
        {
        }
    }

    private sealed class NullWarningDisplay : IWarningDisplay
    {
        public void ShowCountdown(AgentCountdown countdown)
        {
        }

        public void ShowMessage(string text)
        {
        }

        public void Hide()
        {
        }
    }
}
