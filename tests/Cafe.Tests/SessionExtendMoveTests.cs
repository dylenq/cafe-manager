using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class SessionExtendMoveTests : SessionTestBase
{
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    /// <summary>Puts a prepaid session into Grace the way the expiry engine would: segment closed at the expiry instant.</summary>
    private async Task EnterGraceAsync(int sessionId, int graceSeconds = 30)
    {
        var expiry = Factory.Clock.GetUtcNow();
        await UpdateAsync(sessionId, s =>
        {
            s.Status = SessionStatus.Grace;
            s.GraceStartedAtUtc = expiry;
            foreach (var seg in s.Segments.Where(x => x.EndUtc is null))
            {
                seg.EndUtc = expiry;
            }
        });
        Advance(TimeSpan.FromSeconds(graceSeconds));
    }

    [Fact]
    public async Task Extend_after_the_grace_elapsed_but_before_a_tick_ends_the_session_instead_of_reviving_it()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        Advance(30 * Min + TimeSpan.FromMinutes(3)); // past expiry and the 2 min grace, no tick ran

        var extend = await SvcAsync(s => s.ExtendAsync(session.Id, 10));

        Assert.False(extend.Success);
        Assert.Contains("grace period are over", extend.Error);
        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Ended, saved.Status);
        Assert.Equal(SessionEndReason.Expired, saved.EndReason);
        Assert.Equal(30, saved.PurchasedMinutes);
    }

    [Fact]
    public async Task Extend_just_after_expiry_but_before_a_tick_revives_from_grace_at_the_true_expiry()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        Advance(30 * Min + TimeSpan.FromSeconds(20));

        var extend = await SvcAsync(s => s.ExtendAsync(session.Id, 10));

        Assert.True(extend.Success, extend.Error);
        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Active, saved.Status);
        Assert.Equal(Start.AddMinutes(30), saved.Segments.OrderBy(x => x.Id).First().EndUtc);
    }

    [Fact]
    public async Task Pause_after_the_time_ran_out_but_before_a_tick_is_refused_and_does_not_overrun()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        Advance(30 * Min + TimeSpan.FromSeconds(20));

        var pause = await SvcAsync(s => s.PauseAsync(session.Id));

        Assert.False(pause.Success);
        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Grace, saved.Status);
        Assert.Equal(Start.AddMinutes(30), Assert.Single(saved.Segments).EndUtc);
    }

    [Fact]
    public async Task Extend_active_adds_minutes_and_a_quote_projected_from_the_planned_end()
    {
        var station = await StationAsync();
        var session = await PrepaidAsync(station, 60);
        Advance(20 * Min);

        var extend = await SvcAsync(s => s.ExtendAsync(session.Id, 30));

        Assert.True(extend.Success, extend.Error);
        Assert.Equal(3000.00m, extend.Value);
        var saved = await LoadAsync(session.Id);
        Assert.Equal(90, saved.PurchasedMinutes);
        Assert.Equal(9000.00m, saved.ChargedAmount);
        Assert.Equal(70 * 60, Clocked(saved, Factory.Clock.GetUtcNow()).Remaining);
        var evt = saved.Events.Single(e => e.Type == SessionEventType.Extended);
        Assert.StartsWith("Extended by 30 min", evt.Details);
        Assert.Equal(SessionStatus.Active, saved.Status);
    }

    [Fact]
    public async Task Extend_is_priced_at_the_planned_end_not_at_now()
    {
        var typeId = await TypeAsync();
        var station = await StationAsync(typeId: typeId);
        // Happy hour starts exactly when the original 60 minutes end (13:00).
        await RuleAsync(RateRuleKind.FixedHourlyRate, 4000m, new TimeOnly(13, 0), new TimeOnly(15, 0), typeId);
        var session = await PrepaidAsync(station, 60);
        Advance(20 * Min);

        var extend = await SvcAsync(s => s.ExtendAsync(session.Id, 30));

        Assert.Equal(2000.00m, extend.Value);
        Assert.Equal(8000.00m, (await LoadAsync(session.Id)).ChargedAmount);
    }

    [Fact]
    public async Task Extend_paused_raises_remaining_and_stays_paused_without_a_new_segment()
    {
        var station = await StationAsync();
        var session = await PrepaidAsync(station, 60);
        Advance(10 * Min);
        await SvcAsync(s => s.PauseAsync(session.Id));
        Advance(TimeSpan.FromHours(1));

        var extend = await SvcAsync(s => s.ExtendAsync(session.Id, 15));

        Assert.True(extend.Success, extend.Error);
        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Paused, saved.Status);
        Assert.Single(saved.Segments);
        Assert.All(saved.Segments, x => Assert.NotNull(x.EndUtc));
        Assert.Equal(65 * 60, Clocked(saved, Factory.Clock.GetUtcNow()).Remaining);
    }

    [Fact]
    public async Task Extend_in_grace_returns_to_active_with_a_new_segment_and_exact_remaining()
    {
        var station = await StationAsync();
        var session = await PrepaidAsync(station, 60);
        Advance(60 * Min);
        await EnterGraceAsync(session.Id, 45);

        var extend = await SvcAsync(s => s.ExtendAsync(session.Id, 15));

        Assert.True(extend.Success, extend.Error);
        Assert.Equal(1500.00m, extend.Value);
        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Active, saved.Status);
        Assert.Null(saved.GraceStartedAtUtc);
        Assert.Equal(7500.00m, saved.ChargedAmount);
        Assert.Equal(75, saved.PurchasedMinutes);
        Assert.Equal(2, saved.Segments.Count);
        Assert.Null(saved.Segments.Single(x => x.EndUtc is null).EndUtc);
        Assert.Equal(15 * 60, Clocked(saved, Factory.Clock.GetUtcNow()).Remaining);
    }

    [Fact]
    public async Task Extend_rejects_open_ended_bad_minutes_ended_and_unknown_sessions()
    {
        var a = await StationAsync("A");
        var b = await StationAsync("B");
        var open = await OpenEndedAsync(a);
        var prepaid = await PrepaidAsync(b, 60);

        Assert.False((await SvcAsync(s => s.ExtendAsync(open.Id, 30))).Success);
        Assert.False((await SvcAsync(s => s.ExtendAsync(prepaid.Id, 0))).Success);
        Assert.False((await SvcAsync(s => s.ExtendAsync(prepaid.Id, 1441))).Success);
        Assert.Equal(FailureKind.NotFound, (await SvcAsync(s => s.ExtendAsync(99999, 15))).Kind);

        await SvcAsync(s => s.StopAsync(prepaid.Id));
        var ended = await SvcAsync(s => s.ExtendAsync(prepaid.Id, 15));
        Assert.False(ended.Success);

        var saved = await LoadAsync(prepaid.Id);
        Assert.Equal(60, saved.PurchasedMinutes);
        Assert.DoesNotContain(saved.Events, e => e.Type == SessionEventType.Extended);
    }

    [Fact]
    public async Task Move_active_closes_old_segment_opens_new_one_and_keeps_time_and_charge()
    {
        var typeB = await TypeAsync("Premium", 12000m);
        var a = await StationAsync("A");
        var b = await StationAsync("B", typeB);
        var session = await PrepaidAsync(a, 60);
        Advance(20 * Min);
        var seen = new List<int>();
        Factory.Services.GetRequiredService<SessionNotifier>().Changed += seen.Add;

        var move = await SvcAsync(s => s.MoveAsync(session.Id, b));

        Assert.True(move.Success, move.Error);
        var saved = await LoadAsync(session.Id);
        Assert.Equal(b, saved.StationId);
        Assert.Equal(6000.00m, saved.ChargedAmount);
        Assert.Equal(40 * 60, Clocked(saved, Factory.Clock.GetUtcNow()).Remaining);
        Assert.Equal(2, saved.Segments.Count);
        var old = saved.Segments.Single(x => x.StationId == a);
        Assert.Equal(Factory.Clock.GetUtcNow(), old.EndUtc);
        Assert.Null(saved.Segments.Single(x => x.StationId == b).EndUtc);
        var evt = saved.Events.Single(e => e.Type == SessionEventType.Moved);
        Assert.Equal("Moved A -> B", evt.Details);
        Assert.Equal(b, evt.StationId);
        Assert.Contains(a, seen);
        Assert.Contains(b, seen);

        // The original station is immediately free again.
        Assert.True((await SvcAsync(s => s.StartPrepaidAsync(a, 30))).Success);
    }

    [Fact]
    public async Task Move_paused_changes_station_and_opens_the_segment_on_resume()
    {
        var a = await StationAsync("A");
        var b = await StationAsync("B");
        var session = await PrepaidAsync(a, 60);
        Advance(10 * Min);
        await SvcAsync(s => s.PauseAsync(session.Id));

        var move = await SvcAsync(s => s.MoveAsync(session.Id, b));

        Assert.True(move.Success, move.Error);
        var moved = await LoadAsync(session.Id);
        Assert.Equal(b, moved.StationId);
        Assert.Equal(SessionStatus.Paused, moved.Status);
        Assert.Single(moved.Segments);

        await SvcAsync(s => s.ResumeAsync(session.Id));
        var resumed = await LoadAsync(session.Id);
        Assert.Equal(b, resumed.Segments.Single(x => x.EndUtc is null).StationId);
    }

    [Fact]
    public async Task Move_open_ended_across_types_bills_each_segment_at_its_own_rate()
    {
        var typeB = await TypeAsync("Premium", 12000m);
        var a = await StationAsync("A");
        var b = await StationAsync("B", typeB);
        var session = await OpenEndedAsync(a);
        Advance(30 * Min);
        Assert.True((await SvcAsync(s => s.MoveAsync(session.Id, b))).Success);
        Advance(30 * Min);

        var stop = await SvcAsync(s => s.StopAsync(session.Id));

        // 30 min at 6000 plus 30 min at 12000.
        Assert.Equal(9000.00m, stop.Value);
    }

    [Fact]
    public async Task Move_rejects_busy_disabled_unknown_same_grace_and_ended_and_leaves_the_session_untouched()
    {
        var a = await StationAsync("A");
        var busy = await StationAsync("Busy");
        var off = await StationAsync("Off", enabled: false);
        var session = await PrepaidAsync(a, 60);
        await OpenEndedAsync(busy);
        Advance(5 * Min);

        Assert.False((await SvcAsync(s => s.MoveAsync(session.Id, busy))).Success);
        Assert.False((await SvcAsync(s => s.MoveAsync(session.Id, off))).Success);
        Assert.Equal(FailureKind.NotFound, (await SvcAsync(s => s.MoveAsync(session.Id, 99999))).Kind);
        Assert.False((await SvcAsync(s => s.MoveAsync(session.Id, a))).Success);

        var saved = await LoadAsync(session.Id);
        Assert.Equal(a, saved.StationId);
        Assert.Null(Assert.Single(saved.Segments).EndUtc);
        Assert.DoesNotContain(saved.Events, e => e.Type == SessionEventType.Moved);

        var free = await StationAsync("Free");
        await UpdateAsync(session.Id, s => s.Status = SessionStatus.Grace);
        Assert.False((await SvcAsync(s => s.MoveAsync(session.Id, free))).Success);
        await UpdateAsync(session.Id, s => s.Status = SessionStatus.Active);
        await SvcAsync(s => s.StopAsync(session.Id));
        Assert.False((await SvcAsync(s => s.MoveAsync(session.Id, free))).Success);
    }

    [Fact]
    public async Task Concurrent_moves_to_one_idle_target_yield_exactly_one_success()
    {
        var a = await StationAsync("A");
        var b = await StationAsync("B");
        var target = await StationAsync("Target");
        var s1 = await PrepaidAsync(a, 60);
        var s2 = await PrepaidAsync(b, 60);

        var results = await Task.WhenAll(
            SvcAsync(s => s.MoveAsync(s1.Id, target)),
            SvcAsync(s => s.MoveAsync(s2.Id, target)));

        Assert.Equal(1, results.Count(r => r.Success));
        var loser = results.Single(r => !r.Success);
        Assert.Equal(FailureKind.Validation, loser.Kind);
        await using var db = await DbAsync();
        var onTarget = db.Sessions.Count(x => x.StationId == target && x.Status != SessionStatus.Ended);
        Assert.Equal(1, onTarget);
    }
}
