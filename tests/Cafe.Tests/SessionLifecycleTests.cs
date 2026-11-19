using Cafe.Server.Dashboard;
using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class SessionLifecycleTests : SessionTestBase
{
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Open_ended_start_has_no_minutes_zero_charge_open_segment_and_event()
    {
        var station = await StationAsync();

        var session = await OpenEndedAsync(station);

        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionMode.OpenEnded, saved.Mode);
        Assert.Null(saved.PurchasedMinutes);
        Assert.Equal(0m, saved.ChargedAmount);
        Assert.Equal(SessionStatus.Active, saved.Status);
        Assert.Null(Assert.Single(saved.Segments).EndUtc);
        var started = Assert.Single(saved.Events);
        Assert.Equal(SessionEventType.Started, started.Type);
        Assert.Equal("Open-ended", started.Details);
    }

    [Fact]
    public async Task Open_ended_start_enforces_the_same_preconditions_as_prepaid()
    {
        var on = await StationAsync("On");
        var off = await StationAsync("Off", enabled: false);

        Assert.Equal(FailureKind.NotFound, (await SvcAsync(s => s.StartOpenEndedAsync(99999))).Kind);
        Assert.Equal(FailureKind.Validation, (await SvcAsync(s => s.StartOpenEndedAsync(off))).Kind);
        await OpenEndedAsync(on);
        Assert.False((await SvcAsync(s => s.StartOpenEndedAsync(on))).Success);
        Assert.False((await SvcAsync(s => s.StartPrepaidAsync(on, 30))).Success);
    }

    [Fact]
    public async Task Open_ended_stop_bills_actual_minutes_and_records_the_charge()
    {
        var station = await StationAsync();
        var session = await OpenEndedAsync(station);
        Advance(90 * Min);

        var stop = await SvcAsync(s => s.StopAsync(session.Id));

        Assert.True(stop.Success, stop.Error);
        Assert.Equal(9000.00m, stop.Value);
        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Ended, saved.Status);
        Assert.Equal(SessionEndReason.Stopped, saved.EndReason);
        Assert.Equal(9000.00m, saved.ChargedAmount);
        Assert.All(saved.Segments, x => Assert.NotNull(x.EndUtc));
        Assert.Contains("9000.00", saved.Events.Single(e => e.Type == SessionEventType.Stopped).Details);
    }

    [Fact]
    public async Task Open_ended_sub_minute_bills_one_whole_minute_and_zero_seconds_bills_nothing()
    {
        var a = await StationAsync("A");
        var b = await StationAsync("B");
        var quick = await OpenEndedAsync(a);
        var instant = await OpenEndedAsync(b);
        Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(100.00m, (await SvcAsync(s => s.StopAsync(quick.Id))).Value);

        var c = await StationAsync("C");
        var zero = await OpenEndedAsync(c);
        Assert.Equal(0.00m, (await SvcAsync(s => s.StopAsync(zero.Id))).Value);
        Assert.True((await SvcAsync(s => s.StopAsync(instant.Id))).Success);
    }

    [Fact]
    public async Task Pause_then_resume_excludes_paused_time_for_prepaid()
    {
        var station = await StationAsync();
        var session = await PrepaidAsync(station, 60);
        Advance(10 * Min);

        var pause = await SvcAsync(s => s.PauseAsync(session.Id));
        Assert.True(pause.Success, pause.Error);
        var paused = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Paused, paused.Status);
        Assert.All(paused.Segments, x => Assert.NotNull(x.EndUtc));

        Advance(TimeSpan.FromHours(2));
        Assert.Equal(3000, Clocked(await LoadAsync(session.Id), Factory.Clock.GetUtcNow()).Remaining);

        var resume = await SvcAsync(s => s.ResumeAsync(session.Id));
        Assert.True(resume.Success, resume.Error);
        Advance(5 * Min);

        var saved = await LoadAsync(session.Id);
        var (active, remaining) = Clocked(saved, Factory.Clock.GetUtcNow());
        Assert.Equal(SessionStatus.Active, saved.Status);
        Assert.Equal(900, active);
        Assert.Equal(2700, remaining);
        Assert.Equal(2, saved.Segments.Count);
        Assert.Contains(saved.Events, e => e.Type == SessionEventType.Paused);
        Assert.Contains(saved.Events, e => e.Type == SessionEventType.Resumed);
    }

    [Fact]
    public async Task Pause_then_resume_excludes_paused_time_for_open_ended_charge()
    {
        var station = await StationAsync();
        var session = await OpenEndedAsync(station);
        Advance(10 * Min);
        await SvcAsync(s => s.PauseAsync(session.Id));
        Advance(TimeSpan.FromHours(2));
        await SvcAsync(s => s.ResumeAsync(session.Id));
        Advance(5 * Min);

        var stop = await SvcAsync(s => s.StopAsync(session.Id));

        Assert.Equal(1500.00m, stop.Value);
    }

    [Fact]
    public async Task Pause_and_resume_reject_wrong_states_and_unknown_sessions()
    {
        var station = await StationAsync();
        var session = await PrepaidAsync(station, 60);

        Assert.False((await SvcAsync(s => s.ResumeAsync(session.Id))).Success);
        Assert.True((await SvcAsync(s => s.PauseAsync(session.Id))).Success);
        var again = await SvcAsync(s => s.PauseAsync(session.Id));
        Assert.False(again.Success);
        Assert.Equal(FailureKind.Validation, again.Kind);

        await SvcAsync(s => s.ResumeAsync(session.Id));
        await UpdateAsync(session.Id, s => s.Status = SessionStatus.Grace);
        var grace = await SvcAsync(s => s.PauseAsync(session.Id));
        Assert.False(grace.Success);
        Assert.Contains("Time is up", grace.Error);
        Assert.False((await SvcAsync(s => s.ResumeAsync(session.Id))).Success);

        await SvcAsync(s => s.StopAsync(session.Id));
        Assert.False((await SvcAsync(s => s.PauseAsync(session.Id))).Success);
        Assert.False((await SvcAsync(s => s.ResumeAsync(session.Id))).Success);

        Assert.Equal(FailureKind.NotFound, (await SvcAsync(s => s.PauseAsync(99999))).Kind);
        Assert.Equal(FailureKind.NotFound, (await SvcAsync(s => s.ResumeAsync(99999))).Kind);
        Assert.Equal(FailureKind.NotFound, (await SvcAsync(s => s.StopAsync(99999))).Kind);
    }

    [Fact]
    public async Task Stop_from_paused_open_ended_excludes_paused_hours()
    {
        var station = await StationAsync();
        var session = await OpenEndedAsync(station);
        Advance(30 * Min);
        await SvcAsync(s => s.PauseAsync(session.Id));
        Advance(TimeSpan.FromHours(3));

        var stop = await SvcAsync(s => s.StopAsync(session.Id));

        Assert.Equal(3000.00m, stop.Value);
        Assert.Equal(SessionStatus.Ended, (await LoadAsync(session.Id)).Status);
    }

    [Fact]
    public async Task Stop_from_grace_keeps_the_quoted_charge()
    {
        var station = await StationAsync();
        var session = await PrepaidAsync(station, 60);
        Advance(60 * Min);
        await UpdateAsync(session.Id, s =>
        {
            s.Status = SessionStatus.Grace;
            s.GraceStartedAtUtc = Factory.Clock.GetUtcNow();
            foreach (var seg in s.Segments.Where(x => x.EndUtc is null))
            {
                seg.EndUtc = Factory.Clock.GetUtcNow();
            }
        });
        Advance(TimeSpan.FromSeconds(30));

        var stop = await SvcAsync(s => s.StopAsync(session.Id));

        Assert.True(stop.Success, stop.Error);
        Assert.Equal(6000.00m, stop.Value);
    }

    [Fact]
    public async Task Second_stop_is_a_validation_failure_and_writes_no_event()
    {
        var station = await StationAsync();
        var session = await PrepaidAsync(station);
        await SvcAsync(s => s.StopAsync(session.Id));
        var events = (await LoadAsync(session.Id)).Events.Count;

        var again = await SvcAsync(s => s.StopAsync(session.Id));

        Assert.False(again.Success);
        Assert.Equal(FailureKind.Validation, again.Kind);
        Assert.Equal(events, (await LoadAsync(session.Id)).Events.Count);
    }

    [Fact]
    public async Task Stop_bound_to_a_quote_succeeds_when_nothing_changed()
    {
        var open = await OpenEndedAsync(await StationAsync("B"));
        Advance(45 * Min);
        var quote = (await SvcAsync(s => s.QuoteStopAsync(open.Id))).Value!;

        var stop = await SvcAsync(s => s.StopAsync(open.Id, quote.Version, quote.Amount));

        Assert.True(stop.Success, stop.Error);
        Assert.Equal(quote.Amount, stop.Value);
    }

    [Fact]
    public async Task Stop_bound_to_a_quote_is_refused_when_the_session_version_changed()
    {
        var prepaid = await PrepaidAsync(await StationAsync("A"), 60);
        var quote = (await SvcAsync(s => s.QuoteStopAsync(prepaid.Id))).Value!;
        Assert.True((await SvcAsync(s => s.ExtendAsync(prepaid.Id, 30))).Success);

        var stop = await SvcAsync(s => s.StopAsync(prepaid.Id, quote.Version, quote.Amount));

        Assert.False(stop.Success);
        Assert.Equal(SessionService.ChangedMessage, stop.Error);
        Assert.Equal(SessionStatus.Active, (await LoadAsync(prepaid.Id)).Status);
    }

    [Fact]
    public async Task Stop_bound_to_a_quote_is_refused_when_an_open_ended_charge_moved_on()
    {
        var open = await OpenEndedAsync(await StationAsync("B"));
        Advance(45 * Min);
        var quote = (await SvcAsync(s => s.QuoteStopAsync(open.Id))).Value!;
        Advance(10 * Min);

        var stop = await SvcAsync(s => s.StopAsync(open.Id, quote.Version, quote.Amount));

        Assert.Equal(SessionService.ChangedMessage, stop.Error);
        var saved = await LoadAsync(open.Id);
        Assert.Equal(SessionStatus.Active, saved.Status);
        Assert.Null(Assert.Single(saved.Segments).EndUtc);
        var requote = (await SvcAsync(s => s.QuoteStopAsync(open.Id))).Value!;
        Assert.True((await SvcAsync(s => s.StopAsync(open.Id, requote.Version, requote.Amount))).Success);
    }

    [Fact]
    public async Task Stop_quote_matches_stop_and_does_not_mutate()
    {
        var a = await StationAsync("A");
        var b = await StationAsync("B");
        var prepaid = await PrepaidAsync(a, 60);
        var open = await OpenEndedAsync(b);
        Advance(45 * Min);
        var before = (await LoadAsync(open.Id)).Version;

        var prepaidQuote = await SvcAsync(s => s.GetStopQuoteAsync(prepaid.Id));
        var openQuote = await SvcAsync(s => s.GetStopQuoteAsync(open.Id));

        Assert.Equal(6000.00m, prepaidQuote.Value);
        Assert.Equal(4500.00m, openQuote.Value);
        var untouched = await LoadAsync(open.Id);
        Assert.Equal(before, untouched.Version);
        Assert.Equal(SessionStatus.Active, untouched.Status);
        Assert.Single(untouched.Events);
        Assert.Null(Assert.Single(untouched.Segments).EndUtc);

        Assert.Equal(4500.00m, (await SvcAsync(s => s.StopAsync(open.Id))).Value);
        Assert.False((await SvcAsync(s => s.GetStopQuoteAsync(open.Id))).Success);
        Assert.Equal(FailureKind.NotFound, (await SvcAsync(s => s.GetStopQuoteAsync(99999))).Kind);
    }

    [Fact]
    public async Task Concurrent_starts_on_one_station_produce_exactly_one_session()
    {
        var station = await StationAsync();

        var results = await Task.WhenAll(
            SvcAsync(s => s.StartPrepaidAsync(station, 60)),
            SvcAsync(s => s.StartPrepaidAsync(station, 30)));

        Assert.Single(results, r => r.Success);
        Assert.Single(results, r => !r.Success);
        await using var db = await DbAsync();
        Assert.Equal(1, await db.Sessions.CountAsync(s => s.StationId == station && s.Status != SessionStatus.Ended));
    }

    [Fact]
    public async Task Concurrent_prepaid_versus_open_ended_start_produce_exactly_one_session()
    {
        var station = await StationAsync();

        var results = await Task.WhenAll(
            SvcAsync(s => s.StartPrepaidAsync(station, 60)),
            SvcAsync(s => s.StartOpenEndedAsync(station)));

        Assert.Equal(1, results.Count(r => r.Success));
        await using var db = await DbAsync();
        Assert.Equal(1, await db.Sessions.CountAsync(s => s.StationId == station && s.Status != SessionStatus.Ended));
    }

    [Fact]
    public async Task Stale_version_returns_a_validation_failure_rather_than_throwing()
    {
        var station = await StationAsync();
        var session = await PrepaidAsync(station);

        using var scope = Factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<SessionService>();
        svc.BeforeSave = async () =>
        {
            await using var other = await DbAsync();
            await other.Database.ExecuteSqlRawAsync("UPDATE Sessions SET Version = Version + 1 WHERE Id = {0}", session.Id);
        };

        var result = await svc.PauseAsync(session.Id);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.Validation, result.Kind);
        Assert.Contains("Refresh", result.Error);
        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Active, saved.Status);
        Assert.Single(saved.Events);
    }

    [Fact]
    public async Task Open_ended_stop_splits_at_a_happy_hour_rule()
    {
        var typeId = await TypeAsync();
        var station = await StationAsync(typeId: typeId);
        await RuleAsync(RateRuleKind.FixedHourlyRate, 3000m, new TimeOnly(13, 0), new TimeOnly(14, 0), typeId);
        var session = await OpenEndedAsync(station);
        Advance(90 * Min);

        var stop = await SvcAsync(s => s.StopAsync(session.Id));

        // 12:00-13:00 at 6000, 13:00-13:30 at 3000.
        Assert.Equal(7500.00m, stop.Value);
    }

    [Fact]
    public async Task Every_mutation_writes_its_event_in_the_same_save()
    {
        var station = await StationAsync();
        var session = await OpenEndedAsync(station);
        await SvcAsync(s => s.PauseAsync(session.Id));
        await SvcAsync(s => s.ResumeAsync(session.Id));
        await SvcAsync(s => s.StopAsync(session.Id));

        var types = (await LoadAsync(session.Id)).Events.OrderBy(e => e.Id).Select(e => e.Type).ToList();

        Assert.Equal([SessionEventType.Started, SessionEventType.Paused, SessionEventType.Resumed, SessionEventType.Stopped], types);
    }

    [Fact]
    public async Task Dashboard_open_ended_charge_is_a_running_estimate_and_stays_flat_while_paused()
    {
        var station = await StationAsync();
        var session = await OpenEndedAsync(station);

        decimal Charge() => Factory.Services.CreateScope().ServiceProvider.GetRequiredService<DashboardService>()
            .GetCardsAsync().GetAwaiter().GetResult().Single().Session!.ChargeAmount;

        Assert.Equal(0m, Charge());
        Advance(30 * Min);
        Assert.Equal(3000.00m, Charge());
        Advance(30 * Min);
        Assert.Equal(6000.00m, Charge());

        await SvcAsync(s => s.PauseAsync(session.Id));
        Advance(TimeSpan.FromHours(1));
        Assert.Equal(6000.00m, Charge());
    }

    [Fact]
    public async Task Dashboard_prepaid_card_keeps_the_quoted_charge()
    {
        var station = await StationAsync();
        await PrepaidAsync(station, 60);
        Advance(20 * Min);

        using var scope = Factory.Services.CreateScope();
        var card = (await scope.ServiceProvider.GetRequiredService<DashboardService>().GetCardsAsync()).Single();

        Assert.Equal(6000.00m, card.Session!.ChargeAmount);
    }
}
