using Cafe.Agent.Core;
using Cafe.Shared.Agents;
using Cafe.Tests.Infrastructure;

namespace Cafe.Tests;

public class AgentControllerStateTests
{
    [Fact]
    public async Task Fresh_controller_is_locked_and_shows_the_lock_screen_once_before_connecting()
    {
        var a = new UnitAgent();
        await a.Controller.StartAsync();

        Assert.Equal(AgentState.Locked, a.Controller.State);
        Assert.Equal(1, a.Lock.ShowCount);
        Assert.False(a.Controller.IsConnected);
        await a.Controller.DisposeAsync();
    }

    [Fact]
    public async Task Unlock_counts_down_locally_between_server_updates()
    {
        var a = new UnitAgent();
        await a.Controller.Unlock(a.Prepaid(3600));

        Assert.Equal(AgentState.Unlocked, a.Controller.State);
        Assert.True(a.Lock.HideCount >= 1);
        Assert.Equal(3600, a.Controller.RemainingSeconds);

        a.Clock.Advance(TimeSpan.FromSeconds(10));
        a.Controller.Tick();

        Assert.Equal(3590, a.Controller.RemainingSeconds);
    }

    [Fact]
    public async Task Clock_skew_does_not_leak_into_the_countdown()
    {
        var a = new UnitAgent();
        // The agent clock is 7 minutes ahead of the server.
        await a.Controller.Unlock(a.Prepaid(600, clockAhead: TimeSpan.FromMinutes(7)));

        a.Clock.Advance(TimeSpan.FromSeconds(30));
        a.Controller.Tick();

        Assert.Equal(570, a.Controller.RemainingSeconds);
    }

    [Fact]
    public async Task Open_ended_session_counts_elapsed_up_and_never_locks()
    {
        var a = new UnitAgent();
        await a.Controller.Unlock(a.OpenEnded(100));
        a.Controller.HandleDisconnected();

        a.Clock.Advance(TimeSpan.FromSeconds(10));
        a.Controller.Tick();
        Assert.Equal(110, a.Controller.ElapsedSeconds);
        Assert.Null(a.Controller.RemainingSeconds);

        a.Clock.Advance(TimeSpan.FromHours(5));
        a.Controller.Tick();
        Assert.Equal(AgentState.Unlocked, a.Controller.State);
    }

    [Fact]
    public async Task Warning_shows_text_and_a_running_countdown_until_time_is_added()
    {
        var a = new UnitAgent();
        await a.Controller.Unlock(a.Prepaid(600));

        await a.Controller.ShowWarning(10);
        Assert.Contains("10 minutes remaining", a.Warnings.Messages);
        var before = a.Warnings.Countdowns.Count;

        a.Clock.Advance(TimeSpan.FromSeconds(1));
        a.Controller.Tick();
        Assert.Equal(before + 1, a.Warnings.Countdowns.Count);
        Assert.False(a.Warnings.Countdowns[^1].IsGrace);

        await a.Controller.ShowWarning(1);
        Assert.Contains("1 minute remaining", a.Warnings.Messages);

        var hides = a.Warnings.HideCount;
        await a.Controller.SessionUpdated(a.Prepaid(5000));
        Assert.True(a.Warnings.HideCount > hides);
        var count = a.Warnings.Countdowns.Count;
        a.Controller.Tick();
        Assert.Equal(count, a.Warnings.Countdowns.Count);
    }

    [Fact]
    public async Task Grace_hides_lock_shows_red_countdown_and_unlock_returns_to_unlocked()
    {
        var a = new UnitAgent();
        await a.Controller.Unlock(a.Prepaid(10));
        a.Controller.HandleConnected();

        await a.Controller.GraceStarted(120);
        Assert.Equal(AgentState.Grace, a.Controller.State);
        Assert.True(a.Warnings.Countdowns[^1].IsGrace);
        Assert.Equal(120, a.Warnings.Countdowns[^1].RemainingSeconds);

        a.Clock.Advance(TimeSpan.FromSeconds(20));
        a.Controller.Tick();
        Assert.Equal(100, a.Warnings.Countdowns[^1].RemainingSeconds);

        await a.Controller.Unlock(a.Prepaid(1800));
        Assert.Equal(AgentState.Unlocked, a.Controller.State);
        a.Clock.Advance(TimeSpan.FromSeconds(500));
        a.Controller.HandleDisconnected();
        Assert.Equal(AgentState.Unlocked, a.Controller.State);
    }

    [Fact]
    public async Task Lock_after_unlock_locks_clears_the_session_and_relock_does_not_raise_state_changed()
    {
        var a = new UnitAgent();
        await a.Controller.Unlock(a.Prepaid(600));
        await a.Controller.ShowWarning(5);
        var hides = a.Warnings.HideCount;

        await a.Controller.Lock(new LockInfo("PC-01", "Cafe", "Session ended"));

        Assert.Equal(AgentState.Locked, a.Controller.State);
        Assert.Equal("Session ended", a.Lock.LastLock!.Reason);
        Assert.True(a.Warnings.HideCount > hides);
        Assert.Null(a.Controller.RemainingSeconds);

        var changes = a.StateChanges;
        var shows = a.Lock.ShowCount;
        await a.Controller.Lock(new LockInfo("PC-01", "Cafe", "Other reason"));
        Assert.Equal(changes, a.StateChanges);
        Assert.Equal(shows + 1, a.Lock.ShowCount);
        Assert.Equal("Other reason", a.Lock.LastLock!.Reason);
    }

    [Fact]
    public async Task Message_shutdown_and_restart_are_forwarded()
    {
        var a = new UnitAgent();
        await a.Controller.ShowMessage("Closing soon");
        await a.Controller.Shutdown();
        await a.Controller.Restart();

        Assert.Contains("Closing soon", a.Warnings.Messages);
        Assert.Equal(1, a.Power.ShutdownCount);
        Assert.Equal(1, a.Power.RestartCount);
    }

    [Fact]
    public async Task Disconnected_agent_locks_itself_at_zero_but_a_connected_one_waits_for_the_server()
    {
        var a = new UnitAgent();
        a.Controller.HandleConnected();
        await a.Controller.Unlock(a.Prepaid(5));
        a.Clock.Advance(TimeSpan.FromSeconds(6));
        a.Controller.Tick();
        Assert.Equal(AgentState.Unlocked, a.Controller.State);
        Assert.Equal(0, a.Controller.RemainingSeconds);

        a.Controller.HandleDisconnected();
        Assert.Equal(AgentState.Locked, a.Controller.State);
        Assert.Contains("Time is up", a.Lock.LastLock!.Reason);
    }

    [Fact]
    public async Task Disconnected_mid_session_counts_down_then_locks_at_zero()
    {
        var a = new UnitAgent();
        a.Controller.HandleConnected();
        await a.Controller.Unlock(a.Prepaid(5));
        a.Controller.HandleDisconnected();
        Assert.Equal(AgentState.Unlocked, a.Controller.State);

        a.Clock.Advance(TimeSpan.FromSeconds(5));
        a.Controller.Tick();

        Assert.Equal(AgentState.Locked, a.Controller.State);
        Assert.Contains("Time is up", a.Lock.LastLock!.Reason);
    }

    [Fact]
    public async Task Grace_locks_locally_at_zero_only_when_disconnected()
    {
        var connected = new UnitAgent();
        connected.Controller.HandleConnected();
        await connected.Controller.GraceStarted(10);
        connected.Clock.Advance(TimeSpan.FromSeconds(11));
        connected.Controller.Tick();
        Assert.Equal(AgentState.Grace, connected.Controller.State);
        await connected.Controller.Lock(new LockInfo("PC-01", "Cafe", "Ended"));
        Assert.Equal(AgentState.Locked, connected.Controller.State);

        var offline = new UnitAgent();
        await offline.Controller.GraceStarted(10);
        offline.Controller.HandleDisconnected();
        offline.Clock.Advance(TimeSpan.FromSeconds(10));
        offline.Controller.Tick();
        Assert.Equal(AgentState.Locked, offline.Controller.State);
    }

    [Fact]
    public async Task Emergency_unlock_has_no_countdown_and_a_server_lock_clears_it()
    {
        var a = new UnitAgent();
        a.Controller.EmergencyUnlock();

        Assert.Equal(AgentState.Unlocked, a.Controller.State);
        Assert.True(a.Controller.IsEmergencyUnlocked);
        Assert.Null(a.Controller.RemainingSeconds);

        a.Clock.Advance(TimeSpan.FromHours(3));
        a.Controller.HandleDisconnected();
        Assert.Equal(AgentState.Unlocked, a.Controller.State);

        await a.Controller.Lock(new LockInfo("PC-01", "Cafe", "Locked by staff"));
        Assert.Equal(AgentState.Locked, a.Controller.State);
        Assert.False(a.Controller.IsEmergencyUnlocked);
    }

    [Fact]
    public async Task Setting_the_wall_clock_back_does_not_extend_the_offline_countdown()
    {
        var a = new UnitAgent();
        a.Controller.HandleConnected();
        await a.Controller.Unlock(a.Prepaid(60));
        a.Controller.HandleDisconnected();

        // The customer sets the PC clock back by an hour; 60 s really pass.
        a.Clock.SetWallClock(a.Clock.UtcNow - TimeSpan.FromHours(1));
        a.Clock.Advance(TimeSpan.FromSeconds(60));
        a.Controller.Tick();

        Assert.Equal(AgentState.Locked, a.Controller.State);
    }

    [Fact]
    public async Task Setting_the_wall_clock_forward_does_not_end_the_session_early()
    {
        var a = new UnitAgent();
        await a.Controller.Unlock(a.Prepaid(600));
        a.Clock.SetWallClock(a.Clock.UtcNow + TimeSpan.FromHours(5));

        Assert.Equal(600, a.Controller.RemainingSeconds);
    }

    [Fact]
    public async Task Grace_countdown_ignores_wall_clock_changes()
    {
        var a = new UnitAgent();
        await a.Controller.GraceStarted(100);
        a.Clock.SetWallClock(a.Clock.UtcNow - TimeSpan.FromHours(1));
        a.Clock.Advance(TimeSpan.FromSeconds(40));

        Assert.Equal(60, a.Controller.GraceRemainingSeconds);
    }
}
