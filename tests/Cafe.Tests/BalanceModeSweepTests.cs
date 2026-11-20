using Cafe.Server.Dashboard;
using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Shared;
using Cafe.Shared.Agents;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class BalanceModeSweepTests : CustomerTestBase
{
    private static readonly TimeSpan Min = TimeSpan.FromMinutes(1);

    private Task EvalAsync() => Factory.Services.GetRequiredService<IExpiryEvaluator>().EvaluateAsync(CancellationToken.None);

    private async Task<(int StationId, int CustomerId, Session Session)> BalanceSessionAsync(int minutes = 30)
    {
        var station = await StationAsync();
        var customer = await CustomerAsync("Alice");
        await SellAsync(customer, await PackageAsync("2 hours", 120, 100000m));
        return (station, customer, await StartBalanceAsync(station, customer, minutes));
    }

    private async Task<int> LedgerRowsAsync(LedgerReason reason)
    {
        await using var db = await DbAsync();
        return await db.CustomerMinuteLedger.CountAsync(l => l.Reason == reason);
    }

    [Fact]
    public async Task Balance_session_expires_through_grace_like_prepaid_and_never_refunds()
    {
        var (_, customer, session) = await BalanceSessionAsync(30);

        Advance(31 * Min);
        await EvalAsync();
        Assert.Equal(SessionStatus.Grace, (await LoadAsync(session.Id)).Status);

        Advance(3 * Min);
        await EvalAsync();
        await EvalAsync();

        var saved = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Ended, saved.Status);
        Assert.Equal(SessionEndReason.Expired, saved.EndReason);
        Assert.Single(saved.Events, e => e.Type == SessionEventType.Expired);
        Assert.Single(saved.Events, e => e.Type == SessionEventType.ExpiryActionFired);
        Assert.Equal(0, await LedgerRowsAsync(LedgerReason.SessionRefund));
        Assert.Equal(90, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Stop_during_grace_refunds_nothing()
    {
        var (_, customer, session) = await BalanceSessionAsync(30);
        Advance(31 * Min);
        await EvalAsync();

        var stop = await SvcAsync(s => s.StopAsync(session.Id));

        Assert.True(stop.Success, stop.Error);
        Assert.Equal(0, await LedgerRowsAsync(LedgerReason.SessionRefund));
        Assert.Equal(90, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Alerts_and_status_treat_balance_like_prepaid()
    {
        var (stationId, _, _) = await BalanceSessionAsync(30);
        Advance(25 * Min);

        var cards = await InScopeAsync(sp => sp.GetRequiredService<DashboardService>().GetCardsAsync());
        var card = cards.Single(c => c.StationId == stationId);

        Assert.Equal(AlertLevel.Warning, SessionAlerts.GetLevel(card.Session!, Factory.Clock.GetUtcNow()));
        Assert.Equal(StationStatus.Expiring, SessionStatusMapper.Map(SessionStatus.Active, SessionMode.Balance, 300, 10));
        Assert.Equal(StationStatus.InSession, SessionStatusMapper.Map(SessionStatus.Active, SessionMode.Balance, 3000, 10));
    }

    [Fact]
    public async Task Quote_is_the_charged_amount_and_extend_falls_back_to_a_paid_extension()
    {
        var (_, customer, session) = await BalanceSessionAsync(30);
        Advance(5 * Min);

        var quote = await SvcAsync(s => s.GetStopQuoteAsync(session.Id));
        var extend = await SvcAsync(s => s.ExtendAsync(session.Id, 15));

        Assert.Equal(0m, quote.Value);
        Assert.True(extend.Success, extend.Error);
        Assert.Equal(1500m, extend.Value);
        var saved = await LoadAsync(session.Id);
        Assert.Equal(45, saved.PurchasedMinutes);
        Assert.Equal(1500m, saved.ChargedAmount);
        Assert.Equal(90, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Prepaid_and_open_ended_behaviour_is_unchanged()
    {
        var prepaid = await PrepaidAsync(await StationAsync("PC-01"), 60);
        var openEnded = await OpenEndedAsync(await StationAsync("PC-02"));
        Advance(30 * Min);

        var prepaidQuote = await SvcAsync(s => s.GetStopQuoteAsync(prepaid.Id));
        var openQuote = await SvcAsync(s => s.GetStopQuoteAsync(openEnded.Id));
        var extend = await SvcAsync(s => s.ExtendAsync(openEnded.Id, 10));

        Assert.Equal(6000m, prepaidQuote.Value);
        Assert.Equal(3000m, openQuote.Value);
        Assert.False(extend.Success);
        Assert.Equal(SessionMode.Prepaid, (await LoadAsync(prepaid.Id)).Mode);
    }
}

public class BalanceAgentMappingTests : AgentTestBase
{
    [Fact]
    public async Task Balance_session_maps_to_a_prepaid_unlock_with_remaining_seconds()
    {
        var stationId = await PcStationAsync("PC-01", "pc-01");
        var key = await KeyAsync();
        await using var agent = new RecordingAgentClient(Factory, key, "pc-01");
        await agent.StartAsync();
        await WaitUntilAsync(() => agent.Calls.Count >= 1, "initial push");

        using var scope = Factory.Services.CreateScope();
        var customer = (await scope.ServiceProvider.GetRequiredService<Cafe.Server.Customers.CustomerService>()
            .CreateAsync(new Cafe.Server.Customers.CustomerInput { Name = "Alice" })).Value!;
        var package = (await scope.ServiceProvider.GetRequiredService<Cafe.Server.Customers.PackageService>()
            .CreateAsync(new Cafe.Server.Customers.TimePackageInput { Name = "1h", Minutes = 60, Price = 1m })).Value!;
        await scope.ServiceProvider.GetRequiredService<Cafe.Server.Customers.PackageService>()
            .SellAsync(customer.Id, package.Id, PaymentMethod.Cash);
        var start = await scope.ServiceProvider.GetRequiredService<SessionService>().StartFromBalanceAsync(stationId, customer.Id, 30);
        Assert.True(start.Success, start.Error);

        await WaitUntilAsync(() => agent.Calls.Count >= 2, "unlock push");
        var info = (SessionInfo)agent.Calls[1].Args[0]!;
        Assert.Equal("Unlock", agent.Calls[1].Method);
        Assert.Equal(AgentSessionMode.Prepaid, info.Mode);
        Assert.Equal(30 * 60, info.RemainingSeconds);
    }
}
