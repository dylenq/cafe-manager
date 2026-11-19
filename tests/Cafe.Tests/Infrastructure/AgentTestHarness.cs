using Cafe.Agent.Core;
using Cafe.Server.Agents;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Shared.Agents;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cafe.Tests.Infrastructure;

public sealed class FakeLockScreen : ILockScreen
{
    public int ShowCount;
    public int HideCount;
    public LockInfo? LastLock;

    public void Show(LockInfo info)
    {
        Interlocked.Increment(ref ShowCount);
        LastLock = info;
    }

    public void Hide() => Interlocked.Increment(ref HideCount);
}

public sealed class FakeWarningDisplay : IWarningDisplay
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

public sealed class FakePowerControl : IPowerControl
{
    public void Shutdown()
    {
    }

    public void Restart()
    {
    }
}

/// <summary>A real AgentController wired to fakes and to the TestServer.</summary>
public sealed class TestAgent(AgentController controller, FakeLockScreen lockScreen) : IAsyncDisposable
{
    public AgentController Controller { get; } = controller;

    public FakeLockScreen LockScreen { get; } = lockScreen;

    public ValueTask DisposeAsync() => Controller.DisposeAsync();
}

/// <summary>Base for agent integration tests: stations with machine ids, the shared key and agent builders.</summary>
public abstract class AgentTestBase : SessionTestBase
{
    protected async Task<int> PcStationAsync(string name = "PC-01", string? machineId = "pc-01")
    {
        using var scope = Factory.Services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
        {
            Name = name, StationTypeId = await TypeAsync(), Kind = StationKind.PC, AgentMachineId = machineId,
        });
        Assert.True(created.Success, created.Error);
        return created.Value!.Id;
    }

    protected Task<string> KeyAsync() =>
        Factory.Services.GetRequiredService<AgentSettingsService>().GetOrCreateSharedKeyAsync();

    protected async Task<TestAgent> StartAgentAsync(string machineId, string? key = null)
    {
        var lockScreen = new FakeLockScreen();
        var options = new AgentConnectionOptions
        {
            ServerUrl = "http://localhost",
            SharedKey = key ?? await KeyAsync(),
            MachineId = machineId,
            ReconnectDelays = [TimeSpan.Zero, TimeSpan.FromMilliseconds(100)],
            ConfigureHttp = o =>
            {
                o.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            },
        };
        var controller = new AgentController(options, lockScreen, new FakeWarningDisplay(), new FakePowerControl(),
            new SystemAgentClock(), NullLogger<AgentController>.Instance);
        await controller.StartAsync();
        return new TestAgent(controller, lockScreen);
    }

    protected static async Task WaitUntilAsync(Func<bool> condition, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what ?? "condition"}.");
            }

            await Task.Delay(20);
        }
    }
}
