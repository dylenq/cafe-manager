using Cafe.Server.Customers;
using Cafe.Server.Dashboard;
using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>Balance session lifecycle edge cases (CUST-05): refund, extend from balance, move, expiry, double stop.</summary>
public class BalanceSessionLifecycleTests : CustomerTestBase
{
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    private async Task<int> CustomerWithAnyAsync(int minutes)
    {
        var customer = await CustomerAsync();
        await SellAsync(customer, await PackageAsync($"any {minutes}", minutes, 1000m));
        return customer;
    }

    private async Task<List<CustomerMinuteLedger>> LedgerAsync(LedgerReason reason)
    {
        await using var db = await DbAsync();
        return await db.CustomerMinuteLedger.AsNoTracking().Where(l => l.Reason == reason).OrderBy(l => l.Id).ToListAsync();
    }

    private Task EvaluateExpiryAsync() =>
        InScopeAsync(async sp =>
        {
            await sp.GetRequiredService<ExpiryEvaluator>().EvaluateAsync();
            return 0;
        });

    [Fact]
    public async Task Early_stop_refunds_the_unused_whole_minutes_to_the_drawn_bucket()
    {
        var customer = await CustomerWithAnyAsync(120);
        var session = await StartBalanceAsync(await StationAsync(), customer, 60);
        Advance(10 * Min);

        var stop = await SvcAsync(s => s.StopAsync(session.Id));

        Assert.True(stop.Success, stop.Error);
        Assert.Equal(110, await BalanceAsync(customer));
        var refund = Assert.Single(await LedgerAsync(LedgerReason.SessionRefund));
        Assert.Equal(50, refund.MinutesDelta);
    }

    [Fact]
    public async Task Stop_at_59m30s_counts_a_full_minute_used_and_refunds_nothing()
    {
        var customer = await CustomerWithAnyAsync(120);
        var session = await StartBalanceAsync(await StationAsync(), customer, 60);
        Advance(59 * Min + TimeSpan.FromSeconds(30));

        await SvcAsync(s => s.StopAsync(session.Id));

        Assert.Empty(await LedgerAsync(LedgerReason.SessionRefund));
        Assert.Equal(60, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Stop_immediately_refunds_the_whole_purchase()
    {
        var customer = await CustomerWithAnyAsync(120);
        var session = await StartBalanceAsync(await StationAsync(), customer, 60);

        await SvcAsync(s => s.StopAsync(session.Id));

        Assert.Equal(120, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Refund_returns_last_drawn_bucket_first_across_mixed_buckets()
    {
        var typeId = await TypeAsync();
        var customer = await CustomerAsync();
        await SellAsync(customer, await PackageAsync("standard 30", 30, 500m, typeId));
        await SellAsync(customer, await PackageAsync("any 100", 100, 1000m));
        var session = await StartBalanceAsync(await StationAsync(typeId: typeId), customer, 50);
        Advance(20 * Min);

        await SvcAsync(s => s.StopAsync(session.Id));

        Assert.Equal(10, await BalanceAsync(customer, typeId));
        Assert.Equal(100, await BalanceAsync(customer));
        var refunds = await LedgerAsync(LedgerReason.SessionRefund);
        Assert.Equal(2, refunds.Count);
        Assert.Equal(20, refunds.Single(r => r.StationTypeId is null).MinutesDelta);
        Assert.Equal(10, refunds.Single(r => r.StationTypeId == typeId).MinutesDelta);
    }

    [Fact]
    public async Task Paused_then_stopped_refunds_by_active_time_only()
    {
        var customer = await CustomerWithAnyAsync(120);
        var session = await StartBalanceAsync(await StationAsync(), customer, 60);
        Advance(10 * Min);
        Assert.True((await SvcAsync(s => s.PauseAsync(session.Id))).Success);
        Advance(30 * Min);

        await SvcAsync(s => s.StopAsync(session.Id));

        Assert.Equal(110, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Expiry_after_grace_refunds_nothing()
    {
        var customer = await CustomerWithAnyAsync(120);
        var session = await StartBalanceAsync(await StationAsync(), customer, 30);
        Advance(30 * Min + TimeSpan.FromSeconds(1));
        await EvaluateExpiryAsync();
        Advance(5 * Min);
        await EvaluateExpiryAsync();

        var saved = await LoadAsync(session.Id);

        Assert.Equal(SessionStatus.Ended, saved.Status);
        Assert.Equal(SessionEndReason.Expired, saved.EndReason);
        Assert.Empty(await LedgerAsync(LedgerReason.SessionRefund));
        Assert.Equal(90, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Stopping_during_grace_refunds_nothing()
    {
        var customer = await CustomerWithAnyAsync(120);
        var session = await StartBalanceAsync(await StationAsync(), customer, 30);
        Advance(30 * Min + TimeSpan.FromSeconds(20));
        await EvaluateExpiryAsync();
        Assert.Equal(SessionStatus.Grace, (await LoadAsync(session.Id)).Status);

        var stop = await SvcAsync(s => s.StopAsync(session.Id));

        Assert.True(stop.Success, stop.Error);
        Assert.Empty(await LedgerAsync(LedgerReason.SessionRefund));
        Assert.Equal(90, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Double_stop_never_refunds_twice()
    {
        var customer = await CustomerWithAnyAsync(120);
        var session = await StartBalanceAsync(await StationAsync(), customer, 60);
        Advance(10 * Min);
        Assert.True((await SvcAsync(s => s.StopAsync(session.Id))).Success);

        var second = await SvcAsync(s => s.StopAsync(session.Id));

        Assert.False(second.Success);
        Assert.Contains("already ended", second.Error);
        Assert.Single(await LedgerAsync(LedgerReason.SessionRefund));
        Assert.Equal(110, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Stop_with_a_stale_expected_version_writes_nothing()
    {
        var customer = await CustomerWithAnyAsync(120);
        var session = await StartBalanceAsync(await StationAsync(), customer, 60);
        var quote = (await SvcAsync(s => s.QuoteStopAsync(session.Id))).Value!;
        Assert.True((await SvcAsync(s => s.PauseAsync(session.Id))).Success);

        var stop = await SvcAsync(s => s.StopAsync(session.Id, quote.Version, quote.Amount));

        Assert.False(stop.Success);
        Assert.Equal(SessionService.ChangedMessage, stop.Error);
        Assert.Empty(await LedgerAsync(LedgerReason.SessionRefund));
        Assert.NotEqual(SessionStatus.Ended, (await LoadAsync(session.Id)).Status);
    }

    [Fact]
    public async Task Stop_quote_previews_the_refund_the_stop_then_writes()
    {
        var customer = await CustomerWithAnyAsync(120);
        var session = await StartBalanceAsync(await StationAsync(), customer, 60);
        Advance(10 * Min);

        var quote = (await SvcAsync(s => s.QuoteStopAsync(session.Id))).Value!;

        Assert.Equal(0m, quote.Amount);
        Assert.Equal(50, quote.RefundMinutes);
        var stop = await SvcAsync(s => s.StopAsync(session.Id, quote.Version, quote.Amount));
        Assert.True(stop.Success, stop.Error);
        Assert.Equal(50, Assert.Single(await LedgerAsync(LedgerReason.SessionRefund)).MinutesDelta);
    }

    [Fact]
    public async Task Stop_quote_has_no_refund_for_prepaid_and_open_ended()
    {
        var prepaid = await PrepaidAsync(await StationAsync("PC-01"), 60);
        var open = await OpenEndedAsync(await StationAsync("PC-02"));

        Assert.Equal(0, (await SvcAsync(s => s.QuoteStopAsync(prepaid.Id))).Value!.RefundMinutes);
        Assert.Equal(0, (await SvcAsync(s => s.QuoteStopAsync(open.Id))).Value!.RefundMinutes);
    }

    [Fact]
    public async Task Extend_from_balance_debits_more_and_adds_the_minutes()
    {
        var customer = await CustomerWithAnyAsync(100);
        var session = await StartBalanceAsync(await StationAsync(), customer, 60);
        var before = (await LoadAsync(session.Id)).Version;

        var extend = await SvcAsync(s => s.ExtendFromBalanceAsync(session.Id, 30));

        Assert.True(extend.Success, extend.Error);
        Assert.Equal(10, extend.Value);
        var saved = await LoadAsync(session.Id);
        Assert.Equal(90, saved.PurchasedMinutes);
        Assert.Equal(before + 1, saved.Version);
        Assert.Equal(0m, saved.ChargedAmount);
        Assert.Contains(saved.Events, e => e.Type == SessionEventType.Extended && e.Details!.Contains("from balance"));
        Assert.Equal(2, (await LedgerAsync(LedgerReason.SessionDebit)).Count);
        Assert.Equal(10, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Extend_from_balance_beyond_the_balance_fails_and_changes_nothing()
    {
        var customer = await CustomerWithAnyAsync(100);
        var session = await StartBalanceAsync(await StationAsync(), customer, 60);

        var extend = await SvcAsync(s => s.ExtendFromBalanceAsync(session.Id, 41));

        Assert.False(extend.Success);
        Assert.StartsWith(SessionService.InsufficientBalancePrefix, extend.Error);
        Assert.Equal(60, (await LoadAsync(session.Id)).PurchasedMinutes);
        Assert.Single(await LedgerAsync(LedgerReason.SessionDebit));
        Assert.Equal(40, await BalanceAsync(customer));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public async Task Extend_from_balance_validates_the_minutes(int minutes)
    {
        var customer = await CustomerWithAnyAsync(100);
        var session = await StartBalanceAsync(await StationAsync(), customer, 10);

        var extend = await SvcAsync(s => s.ExtendFromBalanceAsync(session.Id, minutes));

        Assert.False(extend.Success);
        Assert.Contains("between", extend.Error);
    }

    [Fact]
    public async Task Extend_from_balance_refuses_a_non_balance_session()
    {
        var session = await PrepaidAsync(await StationAsync(), 60);

        var extend = await SvcAsync(s => s.ExtendFromBalanceAsync(session.Id, 10));

        Assert.False(extend.Success);
        Assert.Equal("Only balance sessions can be extended from balance.", extend.Error);
    }

    [Fact]
    public async Task Extend_from_balance_revives_a_grace_session()
    {
        var customer = await CustomerWithAnyAsync(100);
        var session = await StartBalanceAsync(await StationAsync(), customer, 30);
        Advance(30 * Min + TimeSpan.FromSeconds(20));
        await EvaluateExpiryAsync();
        Assert.Equal(SessionStatus.Grace, (await LoadAsync(session.Id)).Status);

        var extend = await SvcAsync(s => s.ExtendFromBalanceAsync(session.Id, 20));

        Assert.True(extend.Success, extend.Error);
        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Active, saved.Status);
        Assert.Null(saved.GraceStartedAtUtc);
        Assert.Equal(2, saved.Segments.Count);
        Assert.Equal(50, saved.PurchasedMinutes);
    }

    [Fact]
    public async Task Extend_from_balance_after_the_grace_elapsed_is_refused()
    {
        var customer = await CustomerWithAnyAsync(100);
        var session = await StartBalanceAsync(await StationAsync(), customer, 30);
        Advance(30 * Min + 3 * Min);

        var extend = await SvcAsync(s => s.ExtendFromBalanceAsync(session.Id, 20));

        Assert.False(extend.Success);
        Assert.Contains("grace period are over", extend.Error);
        Assert.Single(await LedgerAsync(LedgerReason.SessionDebit));
    }

    [Fact]
    public async Task Extend_from_balance_draws_the_type_bucket_first_and_a_full_stop_reverses_last_drawn_first()
    {
        var typeId = await TypeAsync();
        var customer = await CustomerAsync();
        await SellAsync(customer, await PackageAsync("standard 30", 30, 500m, typeId));
        await SellAsync(customer, await PackageAsync("any 100", 100, 1000m));
        var session = await StartBalanceAsync(await StationAsync(typeId: typeId), customer, 20); // 20 typed
        Assert.True((await SvcAsync(s => s.ExtendFromBalanceAsync(session.Id, 30))).Success); // 10 typed + 20 any

        var debits = await LedgerAsync(LedgerReason.SessionDebit);
        Assert.Equal(-30, debits.Where(d => d.StationTypeId == typeId).Sum(d => d.MinutesDelta));
        Assert.Equal(-20, debits.Where(d => d.StationTypeId is null).Sum(d => d.MinutesDelta));

        await SvcAsync(s => s.StopAsync(session.Id));

        Assert.Equal(30, await BalanceAsync(customer, typeId));
        Assert.Equal(100, await BalanceAsync(customer));
        var refunds = await LedgerAsync(LedgerReason.SessionRefund);
        Assert.Equal(20, refunds[0].MinutesDelta);
        Assert.Null(refunds[0].StationTypeId);
    }

    [Fact]
    public async Task Paid_extension_on_a_balance_session_adds_to_the_charge_at_the_member_rate_and_is_not_refunded()
    {
        var typeId = await TypeAsync("Standard", 6000m);
        await using (var db = await DbAsync())
        {
            await db.StationTypes.Where(t => t.Id == typeId).ExecuteUpdateAsync(u => u.SetProperty(t => t.MemberHourlyRate, 3000m));
        }

        var customer = await CustomerAsync("Member", member: true);
        await SellAsync(customer, await PackageAsync("any 60", 60, 1000m));
        var session = await StartBalanceAsync(await StationAsync(typeId: typeId), customer, 60);

        var extend = await SvcAsync(s => s.ExtendAsync(session.Id, 30));

        Assert.True(extend.Success, extend.Error);
        Assert.Equal(1500m, extend.Value);
        var saved = await LoadAsync(session.Id);
        Assert.Equal(90, saved.PurchasedMinutes);
        Assert.Equal(1500m, saved.ChargedAmount);
        Assert.Single(await LedgerAsync(LedgerReason.SessionDebit));

        // Stop right away: 90 unused but only the 60 balance-funded minutes come back; the charge stays.
        var stop = await SvcAsync(s => s.StopAsync(session.Id));
        Assert.True(stop.Success, stop.Error);
        Assert.Equal(1500m, stop.Value);
        Assert.Equal(60, Assert.Single(await LedgerAsync(LedgerReason.SessionRefund)).MinutesDelta);
        Assert.Equal(60, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Paid_extension_minutes_are_not_refunded_when_balance_minutes_were_used_up_first()
    {
        var customer = await CustomerWithAnyAsync(60);
        var session = await StartBalanceAsync(await StationAsync(), customer, 60);
        Assert.True((await SvcAsync(s => s.ExtendAsync(session.Id, 30))).Success);
        Advance(70 * Min);

        var stop = await SvcAsync(s => s.StopAsync(session.Id));

        Assert.True(stop.Success, stop.Error);
        Assert.Empty(await LedgerAsync(LedgerReason.SessionRefund));
        Assert.Equal(0, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Paid_extension_refunds_only_the_unused_part_of_the_balance_draw()
    {
        var customer = await CustomerWithAnyAsync(60);
        var session = await StartBalanceAsync(await StationAsync(), customer, 60);
        Assert.True((await SvcAsync(s => s.ExtendAsync(session.Id, 30))).Success);
        Advance(20 * Min);

        var stop = await SvcAsync(s => s.StopAsync(session.Id));

        Assert.True(stop.Success, stop.Error);
        Assert.Equal(40, Assert.Single(await LedgerAsync(LedgerReason.SessionRefund)).MinutesDelta);
        Assert.Equal(40, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Move_is_refused_when_type_specific_minutes_would_land_on_another_station_type()
    {
        var standard = await TypeAsync("Standard");
        var vip = await TypeAsync("VIP", 9000m);
        var customer = await CustomerAsync();
        await SellAsync(customer, await PackageAsync("standard 60", 60, 500m, standard));
        var session = await StartBalanceAsync(await StationAsync("PC-01", standard), customer, 30);
        var vipStation = await StationAsync("VIP-01", vip);

        var move = await SvcAsync(s => s.MoveAsync(session.Id, vipStation));

        Assert.False(move.Success);
        Assert.Contains("Standard", move.Error);
        Assert.Contains("VIP", move.Error);
        var saved = await LoadAsync(session.Id);
        Assert.NotEqual(vipStation, saved.StationId);
        Assert.Single(saved.Segments);
    }

    [Fact]
    public async Task Move_is_allowed_for_any_type_funding_and_for_the_same_station_type()
    {
        var standard = await TypeAsync("Standard");
        var vip = await TypeAsync("VIP", 9000m);
        var anyCustomer = await CustomerWithAnyAsync(60);
        var anySession = await StartBalanceAsync(await StationAsync("PC-01", standard), anyCustomer, 30);
        var vipStation = await StationAsync("VIP-01", vip);

        var toVip = await SvcAsync(s => s.MoveAsync(anySession.Id, vipStation));

        Assert.True(toVip.Success, toVip.Error);

        var typedCustomer = await CustomerAsync("Bob");
        await SellAsync(typedCustomer, await PackageAsync("standard 60", 60, 500m, standard));
        var typedSession = await StartBalanceAsync(await StationAsync("PC-02", standard), typedCustomer, 30);
        var sameType = await StationAsync("PC-03", standard);

        var move = await SvcAsync(s => s.MoveAsync(typedSession.Id, sameType));

        Assert.True(move.Success, move.Error);
        Assert.Equal(2, (await LedgerAsync(LedgerReason.SessionDebit)).Count);
    }

    [Fact]
    public async Task Two_concurrent_extends_cannot_overdraw_the_balance()
    {
        var customer = await CustomerWithAnyAsync(160);
        var session = await StartBalanceAsync(await StationAsync(), customer, 60);

        var results = await Task.WhenAll(
            SvcAsync(s => s.ExtendFromBalanceAsync(session.Id, 60)),
            SvcAsync(s => s.ExtendFromBalanceAsync(session.Id, 60)));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(40, await BalanceAsync(customer));
        Assert.Equal(120, (await LoadAsync(session.Id)).PurchasedMinutes);
    }

    private sealed class RecordingSink : ISessionEventSink
    {
        public List<SessionSignal> Signals { get; } = [];

        public Task OnSessionSignalAsync(SessionSignal signal, CancellationToken cancellationToken)
        {
            lock (Signals)
            {
                Signals.Add(signal);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSink : ISessionEventSink
    {
        public Task OnSessionSignalAsync(SessionSignal signal, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Signals_for_a_balance_session_match_a_prepaid_one()
    {
        var recorder = new RecordingSink();
        Factory.ServiceOverrides.Add(services => services.AddSingleton<ISessionEventSink>(recorder));
        var customer = await CustomerWithAnyAsync(100);
        var station = await StationAsync();

        var session = await StartBalanceAsync(station, customer, 30);
        await SvcAsync(s => s.ExtendFromBalanceAsync(session.Id, 20));
        await SvcAsync(s => s.StopAsync(session.Id));

        Assert.Equal(
            [SessionSignalKind.Started, SessionSignalKind.Extended, SessionSignalKind.Stopped],
            recorder.Signals.Select(x => x.Kind).ToArray());
        Assert.Equal(20, recorder.Signals[1].Minutes);
        Assert.All(recorder.Signals, x => Assert.Equal(station, x.StationId));
    }

    [Fact]
    public async Task A_failing_sink_does_not_roll_back_ledger_rows()
    {
        Factory.ServiceOverrides.Add(services => services.AddSingleton<ISessionEventSink, ThrowingSink>());
        var customer = await CustomerWithAnyAsync(100);

        var session = await StartBalanceAsync(await StationAsync(), customer, 30);
        var extend = await SvcAsync(s => s.ExtendFromBalanceAsync(session.Id, 20));

        Assert.True(extend.Success, extend.Error);
        Assert.Equal(2, (await LedgerAsync(LedgerReason.SessionDebit)).Count);
        Assert.Equal(50, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Ledger_stays_consistent_after_a_mixed_history_and_no_bucket_goes_negative()
    {
        var standard = await TypeAsync("Standard");
        var vip = await TypeAsync("VIP", 9000m);
        var customer = await CustomerAsync();
        await SellAsync(customer, await PackageAsync("standard 40", 40, 500m, standard));
        await SellAsync(customer, await PackageAsync("any 100", 100, 1000m));
        var session = await StartBalanceAsync(await StationAsync("PC-01", standard), customer, 50); // 40 typed + 10 any
        Assert.True((await SvcAsync(s => s.ExtendFromBalanceAsync(session.Id, 30))).Success); // 30 any
        Assert.True((await SvcAsync(s => s.ExtendAsync(session.Id, 10))).Success); // paid, no ledger
        Advance(20 * Min);
        var sameType = await StationAsync("PC-02", standard);
        var vipStation = await StationAsync("VIP-01", vip);
        Assert.False((await SvcAsync(s => s.MoveAsync(session.Id, vipStation))).Success);
        Assert.True((await SvcAsync(s => s.MoveAsync(session.Id, sameType))).Success);
        Advance(5 * Min);

        var stop = await SvcAsync(s => s.StopAsync(session.Id));

        Assert.True(stop.Success, stop.Error);
        await using var db = await DbAsync();
        var rows = await db.CustomerMinuteLedger.AsNoTracking().Where(l => l.SessionId == session.Id).ToListAsync();
        var drawn = -rows.Where(r => r.Reason == LedgerReason.SessionDebit).Sum(r => r.MinutesDelta);
        var refunded = rows.Where(r => r.Reason == LedgerReason.SessionRefund).Sum(r => r.MinutesDelta);
        Assert.Equal(80, drawn);
        Assert.Equal(55, refunded); // drawn 80 - used 25: balance minutes are consumed before the paid 10
        Assert.Equal(-(drawn - refunded), rows.Sum(r => r.MinutesDelta));
        var buckets = await db.CustomerMinuteLedger.AsNoTracking()
            .GroupBy(l => new { l.CustomerId, l.StationTypeId })
            .Select(g => new { g.Key.StationTypeId, Sum = g.Sum(l => l.MinutesDelta) })
            .ToListAsync();
        Assert.All(buckets, b => Assert.True(b.Sum >= 0, $"bucket {b.StationTypeId} negative: {b.Sum}"));
        Assert.Equal(140 - 80 + 55, buckets.Sum(b => b.Sum));
    }

    [Fact]
    public async Task A_balance_start_that_cannot_proceed_leaves_no_session_and_no_ledger_row()
    {
        var customer = await CustomerWithAnyAsync(100);
        var station = await StationAsync();
        await PrepaidAsync(station, 30);

        var start = await TryStartBalanceAsync(station, customer, 30);

        Assert.False(start.Success);
        await using var db = await DbAsync();
        Assert.Empty(await db.CustomerMinuteLedger.Where(l => l.Reason == LedgerReason.SessionDebit).ToListAsync());
        Assert.Equal(1, await db.Sessions.CountAsync());
        Assert.Equal(100, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Two_balance_starts_racing_for_one_station_debit_only_once()
    {
        var customer = await CustomerWithAnyAsync(100);
        var station = await StationAsync();

        var results = await Task.WhenAll(TryStartBalanceAsync(station, customer, 30), TryStartBalanceAsync(station, customer, 30));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Single(await LedgerAsync(LedgerReason.SessionDebit));
        Assert.Equal(70, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Dashboard_card_clears_after_stop_and_the_balance_matches_the_ledger()
    {
        var customer = await CustomerWithAnyAsync(100);
        var station = await StationAsync();
        var session = await StartBalanceAsync(station, customer, 60);
        Advance(15 * Min);
        await SvcAsync(s => s.StopAsync(session.Id));

        var cards = await InScopeAsync(sp => sp.GetRequiredService<DashboardService>().GetCardsAsync());
        var balance = await InScopeAsync(sp => sp.GetRequiredService<BalanceService>().GetBalanceAsync(customer));

        Assert.Null(cards.Single(c => c.StationId == station).Session);
        Assert.Equal(await BalanceAsync(customer), balance.TotalMinutes);
        Assert.Equal(85, balance.TotalMinutes);
    }
}
