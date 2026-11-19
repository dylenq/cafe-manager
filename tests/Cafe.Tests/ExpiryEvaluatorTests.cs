using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cafe.Tests;

public class ExpiryEvaluatorTests : SessionTestBase
{
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    private sealed class RecordingHandler : IExpiryActionHandler
    {
        public List<string> Calls { get; } = [];

        public Task ExecuteAsync(Station station, CancellationToken cancellationToken)
        {
            Calls.Add(station.Name);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : IExpiryActionHandler
    {
        public Task ExecuteAsync(Station station, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("device offline");
    }

    private sealed class FlakyHandler(int failures, string message = "flaky") : IExpiryActionHandler
    {
        public int Calls { get; private set; }

        public Task ExecuteAsync(Station station, CancellationToken cancellationToken) =>
            ++Calls <= failures ? throw new InvalidOperationException(message) : Task.CompletedTask;
    }

    private readonly RecordingHandler _handler = new();

    private ExpiryEvaluator Evaluator(params IExpiryActionHandler[] handlers)
    {
        var sp = Factory.Services;
        return new ExpiryEvaluator(
            sp.GetRequiredService<IDbContextFactory<CafeDbContext>>(),
            Factory.Clock,
            sp.GetRequiredService<SessionSettingsService>(),
            sp.GetRequiredService<SessionNotifier>(),
            handlers.Length == 0 ? [_handler] : handlers,
            NullLogger<ExpiryEvaluator>.Instance);
    }

    private Task EvalAsync(ExpiryEvaluator? evaluator = null) => (evaluator ?? Evaluator()).EvaluateAsync(CancellationToken.None);

    private async Task<List<SessionEvent>> EventsAsync(int sessionId, SessionEventType type) =>
        (await LoadAsync(sessionId)).Events.Where(e => e.Type == type).OrderBy(e => e.Id).ToList();

    private async Task SetSettingsAsync(int grace, string warnings)
    {
        var result = await Factory.Services.GetRequiredService<SessionSettingsService>()
            .SaveAsync(new SessionSettingsInput { GracePeriodMinutes = grace, WarningMinutes = warnings });
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task Nothing_is_recorded_before_any_threshold()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        Advance(10 * Min);

        await EvalAsync();

        var saved = await LoadAsync(session.Id);
        Assert.Single(saved.Events);
        Assert.Equal(SessionStatus.Active, saved.Status);
    }

    [Fact]
    public async Task Each_warning_is_recorded_once_at_its_threshold()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        var evaluator = Evaluator();

        Advance(20 * Min);
        await EvalAsync(evaluator);
        await EvalAsync(evaluator);
        Advance(Min);
        await EvalAsync(evaluator);
        Assert.Equal(["10 min remaining"], (await EventsAsync(session.Id, SessionEventType.WarningReached)).Select(e => e.Details));

        Advance(4 * Min);
        await EvalAsync(evaluator);
        Advance(4 * Min);
        await EvalAsync(evaluator);
        await EvalAsync(evaluator);
        Assert.Equal(["10 min remaining", "5 min remaining", "1 min remaining"],
            (await EventsAsync(session.Id, SessionEventType.WarningReached)).Select(e => e.Details));
    }

    [Fact]
    public async Task A_five_minute_session_records_10_and_5_together_then_1_later()
    {
        var session = await PrepaidAsync(await StationAsync(), 5);
        var evaluator = Evaluator();

        await EvalAsync(evaluator);
        Assert.Equal(["10 min remaining", "5 min remaining"],
            (await EventsAsync(session.Id, SessionEventType.WarningReached)).Select(e => e.Details));

        Advance(4 * Min);
        await EvalAsync(evaluator);
        Assert.Equal(3, (await EventsAsync(session.Id, SessionEventType.WarningReached)).Count);
    }

    [Fact]
    public async Task Pause_in_the_warning_window_does_not_repeat_or_expire_and_resume_only_adds_the_1_min_warning()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        var evaluator = Evaluator();
        Advance(25 * Min);
        await EvalAsync(evaluator);
        Advance(TimeSpan.FromMinutes(1));
        await SvcAsync(s => s.PauseAsync(session.Id));
        var before = (await LoadAsync(session.Id)).Events.Count;

        Advance(TimeSpan.FromHours(1));
        await EvalAsync(evaluator);

        var paused = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Paused, paused.Status);
        Assert.Equal(before, paused.Events.Count);

        await SvcAsync(s => s.ResumeAsync(session.Id));
        Advance(3 * Min);
        await EvalAsync(evaluator);

        Assert.Equal(["10 min remaining", "5 min remaining", "1 min remaining"],
            (await EventsAsync(session.Id, SessionEventType.WarningReached)).Select(e => e.Details));
    }

    [Fact]
    public async Task Expiry_moves_to_grace_closes_the_segment_at_the_expiry_instant_and_is_idempotent()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        var charge = session.ChargedAmount;
        var evaluator = Evaluator();
        Advance(30 * Min);

        await EvalAsync(evaluator);
        await EvalAsync(evaluator);

        var saved = await LoadAsync(session.Id);
        var expiry = Start.AddMinutes(30);
        Assert.Equal(SessionStatus.Grace, saved.Status);
        Assert.Equal(expiry, saved.GraceStartedAtUtc);
        Assert.Equal(expiry, Assert.Single(saved.Segments).EndUtc);
        Assert.Equal(charge, saved.ChargedAmount);
        var expired = Assert.Single(await EventsAsync(session.Id, SessionEventType.Expired));
        Assert.Equal(expiry, expired.AtUtc);
        Assert.Empty(_handler.Calls);
    }

    [Fact]
    public async Task Late_detection_still_uses_the_true_expiry_instant()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        Advance(30 * Min + TimeSpan.FromSeconds(45));

        await EvalAsync();

        var saved = await LoadAsync(session.Id);
        Assert.Equal(Start.AddMinutes(30), Assert.Single(await EventsAsync(session.Id, SessionEventType.Expired)).AtUtc);
        var graceRemaining = (saved.GraceStartedAtUtc!.Value.AddMinutes(2) - Factory.Clock.GetUtcNow()).TotalSeconds;
        Assert.Equal(75, graceRemaining);
    }

    [Fact]
    public async Task Grace_elapsed_ends_the_session_expired_and_calls_each_handler_exactly_once()
    {
        var station = await StationAsync("PC-09");
        var session = await PrepaidAsync(station, 30);
        var charge = session.ChargedAmount;
        var evaluator = Evaluator();
        Advance(30 * Min);
        await EvalAsync(evaluator);
        Advance(2 * Min);

        await EvalAsync(evaluator);
        await EvalAsync(evaluator);
        Advance(Min);
        await EvalAsync(evaluator);

        var saved = await LoadAsync(session.Id);
        var expiry = Start.AddMinutes(30);
        Assert.Equal(SessionStatus.Ended, saved.Status);
        Assert.Equal(SessionEndReason.Expired, saved.EndReason);
        Assert.Equal(expiry.AddMinutes(2), saved.EndedAtUtc);
        Assert.Equal(charge, saved.ChargedAmount);
        Assert.Equal(["PC-09"], _handler.Calls);
        var order = saved.Events.OrderBy(e => e.Id).Select(e => e.Type).ToList();
        Assert.True(order.IndexOf(SessionEventType.GraceElapsed) < order.IndexOf(SessionEventType.ExpiryActionFired));
        Assert.Equal(expiry.AddMinutes(2), Assert.Single(await EventsAsync(session.Id, SessionEventType.GraceElapsed)).AtUtc);
        Assert.Single(await EventsAsync(session.Id, SessionEventType.ExpiryActionFired));
    }

    [Fact]
    public async Task Restart_after_three_hours_resolves_everything_in_one_call_with_true_instants()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        Advance(TimeSpan.FromHours(3));

        await EvalAsync();

        var saved = await LoadAsync(session.Id);
        var expiry = Start.AddMinutes(30);
        Assert.Equal(SessionStatus.Ended, saved.Status);
        Assert.Equal(3, (await EventsAsync(session.Id, SessionEventType.WarningReached)).Count);
        Assert.Equal(expiry, Assert.Single(await EventsAsync(session.Id, SessionEventType.Expired)).AtUtc);
        Assert.Equal(expiry.AddMinutes(2), Assert.Single(await EventsAsync(session.Id, SessionEventType.GraceElapsed)).AtUtc);
        Assert.Single(await EventsAsync(session.Id, SessionEventType.ExpiryActionFired));
        Assert.Single(_handler.Calls);
    }

    [Fact]
    public async Task A_session_persisted_in_grace_whose_grace_elapsed_while_down_is_ended()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        Advance(30 * Min);
        await EvalAsync();
        Advance(TimeSpan.FromHours(2));

        await EvalAsync();

        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Ended, saved.Status);
        Assert.Equal(Start.AddMinutes(32), saved.EndedAtUtc);
        Assert.Single(_handler.Calls);
    }

    [Fact]
    public async Task Zero_grace_resolves_expiry_and_grace_in_the_same_evaluation()
    {
        await SetSettingsAsync(0, "10,5,1");
        var session = await PrepaidAsync(await StationAsync(), 30);
        Advance(30 * Min);

        await EvalAsync();

        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Ended, saved.Status);
        Assert.Equal(Start.AddMinutes(30), saved.EndedAtUtc);
        Assert.Single(await EventsAsync(session.Id, SessionEventType.Expired));
        Assert.Single(await EventsAsync(session.Id, SessionEventType.GraceElapsed));
    }

    [Fact]
    public async Task Settings_changed_between_ticks_apply_on_the_next_evaluation()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        var evaluator = Evaluator();
        Advance(30 * Min);
        await EvalAsync(evaluator);
        Advance(3 * Min);
        await SetSettingsAsync(5, "10,5,1");

        await EvalAsync(evaluator);
        Assert.Equal(SessionStatus.Grace, (await LoadAsync(session.Id)).Status);

        await SetSettingsAsync(2, "10,5,1");
        await EvalAsync(evaluator);
        Assert.Equal(SessionStatus.Ended, (await LoadAsync(session.Id)).Status);
    }

    [Fact]
    public async Task Extending_in_grace_returns_to_active_and_the_session_expires_again_later()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        var evaluator = Evaluator();
        Advance(30 * Min);
        await EvalAsync(evaluator);
        Advance(TimeSpan.FromSeconds(30));
        Assert.True((await SvcAsync(s => s.ExtendAsync(session.Id, 10))).Success);

        Advance(TimeSpan.FromMinutes(5));
        await EvalAsync(evaluator);
        var mid = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Active, mid.Status);
        Assert.Empty(_handler.Calls);

        Advance(TimeSpan.FromMinutes(6));
        await EvalAsync(evaluator);
        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Grace, saved.Status);
        Assert.Equal(2, (await EventsAsync(session.Id, SessionEventType.Expired)).Count);
    }

    [Fact]
    public async Task A_warning_rearms_after_an_extension_lifts_remaining_above_it()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        var evaluator = Evaluator();
        Advance(21 * Min);
        await EvalAsync(evaluator);
        Assert.Single(await EventsAsync(session.Id, SessionEventType.WarningReached));

        Assert.True((await SvcAsync(s => s.ExtendAsync(session.Id, 30))).Success);
        await EvalAsync(evaluator);
        Assert.Single(await EventsAsync(session.Id, SessionEventType.WarningReached));

        Advance(30 * Min);
        await EvalAsync(evaluator);
        Assert.Equal(2, (await EventsAsync(session.Id, SessionEventType.WarningReached)).Count(e => e.Details == "10 min remaining"));
    }

    [Fact]
    public async Task Open_ended_and_paused_sessions_never_warn_or_expire()
    {
        var openEnded = await OpenEndedAsync(await StationAsync("A"));
        var paused = await PrepaidAsync(await StationAsync("B"), 30);
        Advance(25 * Min);
        await SvcAsync(s => s.PauseAsync(paused.Id));
        var pausedEvents = (await LoadAsync(paused.Id)).Events.Count;

        Advance(TimeSpan.FromHours(10));
        await EvalAsync();

        var o = await LoadAsync(openEnded.Id);
        Assert.Equal(SessionStatus.Active, o.Status);
        Assert.Single(o.Events);
        var p = await LoadAsync(paused.Id);
        Assert.Equal(SessionStatus.Paused, p.Status);
        Assert.Equal(pausedEvents, p.Events.Count);
        Assert.Empty(_handler.Calls);
    }

    [Fact]
    public async Task A_throwing_handler_does_not_stop_the_next_one_or_the_session_ending_and_is_recorded_as_failed()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        Advance(TimeSpan.FromHours(1));

        await EvalAsync(Evaluator(new ThrowingHandler(), _handler));

        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Ended, saved.Status);
        Assert.Single(_handler.Calls);
        Assert.Empty(await EventsAsync(session.Id, SessionEventType.ExpiryActionFired));
        var failed = Assert.Single(await EventsAsync(session.Id, SessionEventType.ExpiryActionFailed));
        Assert.Contains("failed", failed.Details);
        Assert.Contains("device offline", failed.Details);
    }

    private sealed class HangingHandler : IExpiryActionHandler
    {
        public Task ExecuteAsync(Station station, CancellationToken cancellationToken) =>
            new TaskCompletionSource().Task; // ignores the token on purpose
    }

    [Fact]
    public async Task A_hung_handler_times_out_and_does_not_block_other_sessions()
    {
        var hung = await PrepaidAsync(await StationAsync("A"), 30);
        var other = await PrepaidAsync(await StationAsync("B"), 30);
        Advance(TimeSpan.FromHours(1));
        var evaluator = Evaluator(new HangingHandler(), _handler);
        evaluator.HandlerTimeout = TimeSpan.FromMilliseconds(200);

        var started = DateTime.UtcNow;
        await EvalAsync(evaluator);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(SessionStatus.Ended, (await LoadAsync(other.Id)).Status);
        Assert.Equal(SessionStatus.Ended, (await LoadAsync(hung.Id)).Status);
        Assert.Contains("timed out", Assert.Single(await EventsAsync(hung.Id, SessionEventType.ExpiryActionFailed)).Details);
        Assert.Equal(2, _handler.Calls.Count);
    }

    [Fact]
    public async Task Handlers_missed_by_a_crash_after_the_Ended_commit_run_on_the_next_pass()
    {
        var session = await PrepaidAsync(await StationAsync("PC-07"), 30);
        Advance(TimeSpan.FromHours(1));
        await EvalAsync();
        Assert.Equal(["PC-07"], _handler.Calls);

        // Simulate the process dying after the Ended commit but before the marker was written.
        await using (var db = await DbAsync())
        {
            await db.SessionEvents.Where(e => e.SessionId == session.Id && e.Type == SessionEventType.ExpiryActionFired).ExecuteDeleteAsync();
        }

        await EvalAsync();
        await EvalAsync();

        Assert.Equal(["PC-07", "PC-07"], _handler.Calls);
        Assert.Single(await EventsAsync(session.Id, SessionEventType.ExpiryActionFired));
    }

    [Fact]
    public async Task A_failed_run_is_retried_after_the_delay_and_stops_once_it_succeeds()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        Advance(TimeSpan.FromHours(1));
        var flaky = new FlakyHandler(failures: 2);
        var evaluator = Evaluator(flaky);

        await EvalAsync(evaluator);
        await EvalAsync(evaluator);
        Assert.Equal(1, flaky.Calls);

        Advance(ExpiryEvaluator.ActionRetryDelay);
        await EvalAsync(evaluator);
        Advance(ExpiryEvaluator.ActionRetryDelay);
        await EvalAsync(evaluator);
        await EvalAsync(evaluator);

        Assert.Equal(3, flaky.Calls);
        Assert.Equal(2, (await EventsAsync(session.Id, SessionEventType.ExpiryActionFailed)).Count);
        Assert.Single(await EventsAsync(session.Id, SessionEventType.ExpiryActionFired));
    }

    [Fact]
    public async Task Retries_stop_after_the_maximum_attempts_with_a_gave_up_marker()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        Advance(TimeSpan.FromHours(1));
        var evaluator = Evaluator(new ThrowingHandler());

        for (var i = 0; i < ExpiryEvaluator.MaxActionAttempts + 3; i++)
        {
            await EvalAsync(evaluator);
            Advance(ExpiryEvaluator.ActionRetryDelay);
        }

        Assert.Equal(ExpiryEvaluator.MaxActionAttempts, (await EventsAsync(session.Id, SessionEventType.ExpiryActionFailed)).Count);
        var fired = Assert.Single(await EventsAsync(session.Id, SessionEventType.ExpiryActionFired));
        Assert.Contains("gave up", fired.Details);
    }

    [Fact]
    public async Task Failure_details_are_truncated_to_the_column_length()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        Advance(TimeSpan.FromHours(1));

        await EvalAsync(Evaluator(new FlakyHandler(failures: 1, message: new string('x', 1000))));

        var failed = Assert.Single(await EventsAsync(session.Id, SessionEventType.ExpiryActionFailed));
        Assert.Equal(300, failed.Details.Length);
    }

    [Fact]
    public async Task Sessions_that_expired_more_than_a_day_ago_are_not_revisited()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        Advance(TimeSpan.FromHours(1));
        await EvalAsync(Evaluator(new FlakyHandler(failures: 100)));
        Assert.Single(await EventsAsync(session.Id, SessionEventType.ExpiryActionFailed));

        Advance(TimeSpan.FromHours(25));
        var late = new RecordingHandler();
        await EvalAsync(Evaluator(late));

        Assert.Empty(late.Calls);
    }

    [Fact]
    public async Task One_failing_session_does_not_block_the_others()
    {
        var first = await PrepaidAsync(await StationAsync("A"), 30);
        var second = await PrepaidAsync(await StationAsync("B"), 30);
        Advance(TimeSpan.FromHours(1));
        var evaluator = Evaluator();
        var calls = 0;
        evaluator.BeforeSave = () => ++calls == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask;

        await EvalAsync(evaluator);

        Assert.Equal(SessionStatus.Active, (await LoadAsync(first.Id)).Status);
        Assert.Equal(SessionStatus.Ended, (await LoadAsync(second.Id)).Status);
    }

    [Fact]
    public async Task Changed_is_raised_for_the_station_on_each_change_and_not_when_nothing_changed()
    {
        var station = await StationAsync();
        var session = await PrepaidAsync(station, 30);
        var raised = new List<int>();
        var notifier = Factory.Services.GetRequiredService<SessionNotifier>();
        void Handler(int id) => raised.Add(id);
        notifier.Changed += Handler;
        try
        {
            var evaluator = Evaluator();
            await EvalAsync(evaluator);
            Assert.Empty(raised);

            Advance(20 * Min);
            await EvalAsync(evaluator);
            Assert.Equal([station], raised);

            Advance(10 * Min);
            await EvalAsync(evaluator);
            Assert.Equal(SessionStatus.Grace, (await LoadAsync(session.Id)).Status);
            Assert.Equal([station, station], raised);

            raised.Clear();
            Advance(2 * Min);
            await EvalAsync(evaluator);
            Assert.NotEmpty(raised);
            Assert.All(raised, r => Assert.Equal(station, r));

            raised.Clear();
            await EvalAsync(evaluator);
            Assert.Empty(raised);
        }
        finally
        {
            notifier.Changed -= Handler;
        }
    }

    [Fact]
    public async Task A_concurrent_stop_makes_the_evaluator_skip_without_duplicate_events()
    {
        var session = await PrepaidAsync(await StationAsync(), 30);
        Advance(TimeSpan.FromHours(1));
        var evaluator = Evaluator();
        evaluator.BeforeSave = async () =>
        {
            evaluator.BeforeSave = null;
            Assert.True((await SvcAsync(s => s.StopAsync(session.Id))).Success);
        };

        await EvalAsync(evaluator);

        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionEndReason.Stopped, saved.EndReason);
        Assert.DoesNotContain(saved.Events, e => e.Type is SessionEventType.Expired or SessionEventType.GraceElapsed or SessionEventType.ExpiryActionFired);
        Assert.Empty(_handler.Calls);
    }
}
