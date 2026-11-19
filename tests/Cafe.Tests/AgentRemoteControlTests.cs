using Cafe.Server.Agents;
using Cafe.Server.Data;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Shared.Agents;
using Cafe.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cafe.Tests;

public class AgentRemoteControlTests : AgentTestBase
{
    private AgentRemoteControlService Remote => Factory.Services.GetRequiredService<AgentRemoteControlService>();

    private AgentCommandLogService LogService => Factory.Services.GetRequiredService<AgentCommandLogService>();

    private async Task<RecordingAgentClient> ConnectAsync(string machineId)
    {
        var recorder = new RecordingAgentClient(Factory, await KeyAsync(), machineId);
        await recorder.StartAsync();
        await WaitUntilAsync(() => recorder.Calls.Count >= 1, $"initial push for {machineId}");
        return recorder;
    }

    private async Task<List<AgentCommandLog>> LogsAsync()
    {
        await using var db = await DbAsync();
        return await db.AgentCommandLogs.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
    }

    [Fact]
    public async Task Lock_is_delivered_and_logged()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("pc-01");

        var result = await Remote.SendAsync(stationId, AgentCommandKind.Lock);

        Assert.True(result.Success, result.Error);
        await WaitUntilAsync(() => agent.Count("Lock") == 2, "manual Lock");
        var info = (LockInfo)agent.Calls.Last().Args[0]!;
        Assert.Equal("Locked by staff", info.Reason);
        Assert.Equal("PC-01", info.StationName);
        var log = Assert.Single(await LogsAsync());
        Assert.Equal(AgentCommandKind.Lock, log.Command);
        Assert.Equal("PC-01", log.StationName);
        Assert.Equal(stationId, log.StationId);
        Assert.Equal(Factory.Clock.GetUtcNow(), log.AtUtc);
        Assert.Equal("Delivered", log.Details);
    }

    [Fact]
    public async Task Unlock_message_shutdown_and_restart_reach_the_agent()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("pc-01");

        Assert.True((await Remote.SendAsync(stationId, AgentCommandKind.Unlock)).Success);
        Assert.True((await Remote.SendAsync(stationId, AgentCommandKind.Message, "  Closing soon  ")).Success);
        Assert.True((await Remote.SendAsync(stationId, AgentCommandKind.Shutdown)).Success);
        Assert.True((await Remote.SendAsync(stationId, AgentCommandKind.Restart)).Success);

        await WaitUntilAsync(() => agent.Calls.Count >= 5, "four manual commands");
        Assert.Equal(["Lock", "Unlock", "ShowMessage", "Shutdown", "Restart"], agent.Calls.Select(c => c.Method).ToArray());
        var unlock = (SessionInfo)agent.Calls[1].Args[0]!;
        Assert.Equal(AgentSessionMode.OpenEnded, unlock.Mode);
        Assert.Null(unlock.RemainingSeconds);
        Assert.Equal(0, unlock.ElapsedSeconds);
        Assert.Equal("Closing soon", (string)agent.Calls[2].Args[0]!);
        Assert.Equal(4, (await LogsAsync()).Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("\u0001\u0002\t\r")]
    public async Task Blank_or_control_only_messages_are_rejected_without_delivery(string? text)
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("pc-01");

        var result = await Remote.SendAsync(stationId, AgentCommandKind.Message, text);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.Validation, result.Kind);
        await Task.Delay(150);
        Assert.Single(agent.Calls);
    }

    [Fact]
    public async Task Message_over_500_characters_is_rejected_and_500_is_accepted()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        await using var agent = await ConnectAsync("pc-01");

        Assert.False((await Remote.SendAsync(stationId, AgentCommandKind.Message, new string('a', 501))).Success);
        Assert.True((await Remote.SendAsync(stationId, AgentCommandKind.Message, new string('b', 500))).Success);

        await WaitUntilAsync(() => agent.Count("ShowMessage") == 1, "long message");
        var stored = (await LogsAsync()).Last();
        Assert.True(stored.Details.Length <= 300);
        Assert.EndsWith("…", stored.Details);
        Assert.Equal(500, ((string)agent.Calls.Last().Args[0]!).Length); // the full text still goes to the agent
    }

    [Fact]
    public async Task Offline_agent_is_refused_and_logged()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");

        var result = await Remote.SendAsync(stationId, AgentCommandKind.Shutdown);

        Assert.False(result.Success);
        Assert.Equal("PC agent is offline", result.Error);
        var log = Assert.Single(await LogsAsync());
        Assert.Equal("Not sent: agent offline", log.Details);
        Assert.Equal(AgentCommandKind.Shutdown, log.Command);
    }

    [Fact]
    public async Task Console_pc_without_agent_and_unknown_station_are_refused()
    {
        var typeId = await TypeAsync();
        int consoleId;
        using (var scope = Factory.Services.CreateScope())
        {
            consoleId = (await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
            {
                Name = "PS5-1", StationTypeId = typeId, Kind = StationKind.Console,
            })).Value!.Id;
        }

        var noAgentId = await PcStationAsync("PC-02", machineId: null);

        var console = await Remote.SendAsync(consoleId, AgentCommandKind.Lock);
        var noAgent = await Remote.SendAsync(noAgentId, AgentCommandKind.Lock);
        var unknown = await Remote.SendAsync(9999, AgentCommandKind.Lock);

        Assert.Equal(FailureKind.Validation, console.Kind);
        Assert.Equal(FailureKind.Validation, noAgent.Kind);
        Assert.Equal(FailureKind.NotFound, unknown.Kind);
        Assert.False(string.IsNullOrWhiteSpace(console.Error));
        Assert.False(string.IsNullOrWhiteSpace(noAgent.Error));
        Assert.Equal(2, (await LogsAsync()).Count); // unknown station writes nothing
    }

    [Fact]
    public async Task List_is_newest_first_filters_by_station_and_caps_at_200()
    {
        var a = await PcStationAsync("PC-01", "pc-01");
        var b = await PcStationAsync("PC-02", "pc-02");
        await Remote.SendAsync(a, AgentCommandKind.Lock);
        Advance(TimeSpan.FromMinutes(1));
        await Remote.SendAsync(b, AgentCommandKind.Restart);
        Advance(TimeSpan.FromMinutes(1));
        await Remote.SendAsync(a, AgentCommandKind.Shutdown);

        var all = await LogService.ListAsync();
        var onlyA = await LogService.ListAsync(a);

        Assert.Equal([AgentCommandKind.Shutdown, AgentCommandKind.Restart, AgentCommandKind.Lock], all.Select(l => l.Command).ToArray());
        Assert.Equal([AgentCommandKind.Shutdown, AgentCommandKind.Lock], onlyA.Select(l => l.Command).ToArray());

        await using (var db = await DbAsync())
        {
            for (var i = 0; i < 210; i++)
            {
                db.AgentCommandLogs.Add(new AgentCommandLog { StationId = a, StationName = "PC-01", Command = AgentCommandKind.Lock, AtUtc = Start.AddDays(-1).AddSeconds(i), Details = "x" });
            }

            await db.SaveChangesAsync();
        }

        Assert.Equal(200, (await LogService.ListAsync(null, 1000)).Count);
    }

    [Fact]
    public async Task A_delivery_exception_returns_a_friendly_failure_and_logs_without_the_stack_trace()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        var registry = Factory.Services.GetRequiredService<AgentRegistry>();
        registry.Register("pc-01", "conn-1", "test", () => { });
        var service = new AgentRemoteControlService(new ThrowingHub(), registry,
            Factory.Services.GetRequiredService<AgentSettingsService>(),
            Factory.Services.GetRequiredService<IDbContextFactory<CafeDbContext>>(),
            Factory.Clock, NullLogger<AgentRemoteControlService>.Instance);

        var result = await service.SendAsync(stationId, AgentCommandKind.Shutdown);

        Assert.False(result.Success);
        Assert.DoesNotContain("   at ", result.Error);
        var log = Assert.Single(await LogsAsync());
        Assert.StartsWith("Failed: ", log.Details);
        Assert.Contains("connection closed", log.Details);
        Assert.DoesNotContain("   at ", log.Details);
    }

    private sealed class ThrowingHub : IHubContext<AgentHub, IAgentClient>
    {
        public IHubClients<IAgentClient> Clients { get; } = new ThrowingClients();

        public IGroupManager Groups => throw new NotSupportedException();

        private sealed class ThrowingClients : IHubClients<IAgentClient>
        {
            private static readonly IAgentClient Throwing = new ThrowingClient();

            public IAgentClient All => Throwing;

            public IAgentClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => Throwing;

            public IAgentClient Client(string connectionId) => Throwing;

            public IAgentClient Clients(IReadOnlyList<string> connectionIds) => Throwing;

            public IAgentClient Group(string groupName) => Throwing;

            public IAgentClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Throwing;

            public IAgentClient Groups(IReadOnlyList<string> groupNames) => Throwing;

            public IAgentClient User(string userId) => Throwing;

            public IAgentClient Users(IReadOnlyList<string> userIds) => Throwing;
        }

        private sealed class ThrowingClient : IAgentClient
        {
            private static Task Fail() => Task.FromException(new InvalidOperationException("The connection closed mid-send: connection closed"));

            public Task Lock(LockInfo info) => Fail();

            public Task Unlock(SessionInfo info) => Fail();

            public Task SessionUpdated(SessionInfo info) => Fail();

            public Task ShowWarning(int minutesRemaining) => Fail();

            public Task GraceStarted(int graceSeconds) => Fail();

            public Task ShowMessage(string text) => Fail();

            public Task Shutdown() => Fail();

            public Task Restart() => Fail();
        }
    }
}
