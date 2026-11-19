using System.Net;
using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class SessionHistoryTests : SessionTestBase
{
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    private async Task<List<SessionHistoryRow>> RecentAsync(int? stationId = null, int take = 200)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SessionHistoryService>().GetRecentAsync(stationId, take);
    }

    [Fact]
    public async Task Recent_sessions_are_newest_first_with_station_mode_charge_and_active_time()
    {
        var a = await StationAsync("A");
        var b = await StationAsync("B");
        var first = await PrepaidAsync(a, 60);
        Advance(10 * Min);
        await SvcAsync(s => s.StopAsync(first.Id));
        Advance(5 * Min);
        var second = await OpenEndedAsync(b);
        Advance(20 * Min);

        var rows = await RecentAsync();

        Assert.Equal([second.Id, first.Id], rows.Select(r => r.SessionId));
        var live = rows[0];
        Assert.Equal("B", live.StationName);
        Assert.Equal(SessionMode.OpenEnded, live.Mode);
        Assert.Equal(SessionStatus.Active, live.Status);
        Assert.Equal(20 * 60, live.ActiveSeconds);
        var ended = rows[1];
        Assert.Equal(SessionEndReason.Stopped, ended.EndReason);
        Assert.Equal(10 * 60, ended.ActiveSeconds);
        Assert.Equal(6000.00m, ended.ChargedAmount);
        Assert.NotNull(ended.EndedAtUtc);
    }

    [Fact]
    public async Task Result_is_capped_at_200_even_when_more_are_requested()
    {
        var station = await StationAsync();
        await using (var db = await DbAsync())
        {
            for (var i = 0; i < 205; i++)
            {
                db.Sessions.Add(new Session
                {
                    StationId = station, Mode = SessionMode.OpenEnded, Status = SessionStatus.Ended,
                    StartedAtUtc = Start.AddMinutes(-i - 1), EndedAtUtc = Start.AddMinutes(-i), EndReason = SessionEndReason.Stopped,
                });
            }

            await db.SaveChangesAsync();
        }

        var rows = await RecentAsync(take: 1000);

        Assert.Equal(200, rows.Count);
        Assert.True(rows.Zip(rows.Skip(1), (x, y) => x.StartedAtUtc >= y.StartedAtUtc).All(ok => ok));
        Assert.Equal(5, (await RecentAsync(take: 5)).Count);
    }

    [Fact]
    public async Task Events_are_time_ordered_and_cover_the_whole_scripted_session()
    {
        var a = await StationAsync("A");
        var b = await StationAsync("B");
        var session = await PrepaidAsync(a, 60);
        Advance(5 * Min);
        await SvcAsync(s => s.PauseAsync(session.Id));
        Advance(5 * Min);
        await SvcAsync(s => s.ResumeAsync(session.Id));
        Advance(5 * Min);
        await SvcAsync(s => s.ExtendAsync(session.Id, 15));
        Advance(5 * Min);
        await SvcAsync(s => s.MoveAsync(session.Id, b));
        Advance(5 * Min);
        await SvcAsync(s => s.StopAsync(session.Id));

        var row = Assert.Single(await RecentAsync());

        Assert.Equal(
            [SessionEventType.Started, SessionEventType.Paused, SessionEventType.Resumed, SessionEventType.Extended, SessionEventType.Moved, SessionEventType.Stopped],
            row.Events.Select(e => e.Type));
        Assert.True(row.Events.Zip(row.Events.Skip(1), (x, y) => x.AtUtc <= y.AtUtc).All(ok => ok));
        Assert.Equal("B", row.Events.Single(e => e.Type == SessionEventType.Moved).StationName);
        Assert.Equal("A", row.Events[0].StationName);
    }

    [Fact]
    public async Task Station_filter_includes_sessions_that_moved_through_the_station()
    {
        var a = await StationAsync("A");
        var b = await StationAsync("B");
        var c = await StationAsync("C");
        var moved = await PrepaidAsync(a, 60);
        await OpenEndedAsync(c);
        Advance(5 * Min);
        await SvcAsync(s => s.MoveAsync(moved.Id, b));

        Assert.Equal([moved.Id], (await RecentAsync(a)).Select(r => r.SessionId));
        Assert.Equal([moved.Id], (await RecentAsync(b)).Select(r => r.SessionId));
        Assert.Single(await RecentAsync(c));
        Assert.Equal(2, (await RecentAsync()).Count);
    }

    [Fact]
    public async Task Anonymous_request_redirects_to_login()
    {
        using var auth = await Factory.CreateAuthenticatedClientAsync();
        var anonymous = Factory.CreateNoRedirectClient();

        var response = await anonymous.GetAsync("/sessions");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString.Replace("http://localhost", ""));
    }

    [Fact]
    public async Task Page_shows_empty_state_then_the_station_after_a_session_ran()
    {
        using var client = await Factory.CreateAuthenticatedClientAsync();

        var empty = await client.GetAsync("/sessions");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Contains("No sessions yet", await empty.Content.ReadAsStringAsync());

        var station = await StationAsync("Alpha-One");
        var session = await PrepaidAsync(station, 60);
        await SvcAsync(s => s.StopAsync(session.Id));

        var body = await client.GetStringAsync("/sessions");
        Assert.Contains("Alpha-One", body);
        Assert.Contains("Prepaid", body);
        Assert.Contains("Stopped", body);
        Assert.DoesNotContain("No sessions yet", body);
    }
}
