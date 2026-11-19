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

        // A fault on a non-UI thread cannot be recovered in-process: .NET ends the process on an unhandled thread-pool
        // exception, so we log it and rely on the watchdog (the Phase 7 scheduled task restarts the agent when it exits;
        // the agent locks again on start). Unobserved task exceptions are logged and observed so they never escalate.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var type = args.ExceptionObject is Exception ex ? ex.GetType().Name : "unknown";
            var message = args.ExceptionObject is Exception ex2 ? ex2.Message : string.Empty;
            _logger?.LogCritical("Unhandled exception (terminating: {Terminating}): {Type}: {Message}", args.IsTerminating, type, message);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError("Unobserved task exception: {Type}: {Message}", args.Exception.GetType().Name, args.Exception.GetBaseException().Message);
            args.SetObserved();
        };

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

    private ILockScreen CreateLockScreen(AgentSettings? settings) =>
        new WpfLockScreen(
            Dispatcher,
            _loggerFactory!.CreateLogger("LockScreen"),
            settings?.EmergencyUnlockCodeHash ?? string.Empty,
            OnEmergencyUnlock);

    private IWarningDisplay CreateWarningDisplay() =>
        new WpfWarningDisplay(Dispatcher, _loggerFactory!.CreateLogger("WarningDisplay"));

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

        // Never block the UI thread on the async dispose (the SignalR callbacks and the dispose continuations would
        // wait for a thread that is waiting for them). Hide the screens first (queued on the dispatcher, so the
        // operator sees the unlock at once), then dispose off the dispatcher and shut down back on it.
        _lockScreen?.Hide();
        _warnings?.Hide();

        var controller = _controller;
        var logger = _logger;
        var loggerFactory = _loggerFactory;
        _ = Task.Run(async () =>
        {
            try
            {
                if (controller is not null)
                {
                    await controller.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning("Error while stopping the agent: {Reason}", ex.GetType().Name);
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    loggerFactory?.Dispose();
                    Shutdown();
                }).Task.ConfigureAwait(false);
            }
        });
    }
}
