using Cafe.Server.Customers;
using Cafe.Server.Dashboard;
using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class CustomerBalanceTracerTests : CustomerTestBase
{
    [Fact]
    public async Task Selling_a_package_writes_sale_payment_and_ledger_credit()
    {
        var customer = await CustomerAsync("Alice");
        var package = await PackageAsync("2 hours", 120, 100000m);

        var sale = await SellAsync(customer, package, PaymentMethod.Cash);

        await using var db = await DbAsync();
        var saved = await db.PackageSales.SingleAsync();
        Assert.Equal(sale.Id, saved.Id);
        var payment = await db.Payments.SingleAsync();
        Assert.Equal(100000m, payment.Amount);
        Assert.Equal(PaymentMethod.Cash, payment.Method);
        Assert.Equal(saved.Id, payment.PackageSaleId);
        var ledger = await db.CustomerMinuteLedger.SingleAsync();
        Assert.Equal(120, ledger.MinutesDelta);
        Assert.Equal(LedgerReason.PackagePurchase, ledger.Reason);
        Assert.Null(ledger.StationTypeId);
        Assert.Equal(saved.Id, ledger.PackageSaleId);
        Assert.Equal(120, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Balance_start_debits_ledger_and_card_carries_customer()
    {
        var station = await StationAsync();
        var customer = await CustomerAsync("Alice");
        await SellAsync(customer, await PackageAsync("2 hours", 120, 100000m));

        var session = await StartBalanceAsync(station, customer, 60);

        var loaded = await LoadAsync(session.Id);
        Assert.Equal(SessionMode.Balance, loaded.Mode);
        Assert.Equal(60, loaded.PurchasedMinutes);
        Assert.Equal(0m, loaded.ChargedAmount);
        Assert.Equal(customer, loaded.CustomerId);
        Assert.Contains(loaded.Events, e => e.Type == SessionEventType.Started);
        await using var db = await DbAsync();
        var debit = await db.CustomerMinuteLedger.SingleAsync(l => l.Reason == LedgerReason.SessionDebit);
        Assert.Equal(-60, debit.MinutesDelta);
        Assert.Equal(session.Id, debit.SessionId);
        Assert.Equal(60, await BalanceAsync(customer));

        var cards = await InScopeAsync(sp => sp.GetRequiredService<DashboardService>().GetCardsAsync());
        var card = cards.Single(c => c.StationId == station);
        Assert.Equal(SessionMode.Balance, card.Session!.Mode);
        Assert.Equal("Alice", card.Session.CustomerName);
        Assert.False(card.Session.IsMemberPricing);
    }

    [Fact]
    public async Task Early_stop_refunds_unused_whole_minutes()
    {
        var station = await StationAsync();
        var customer = await CustomerAsync("Alice");
        await SellAsync(customer, await PackageAsync("2 hours", 120, 100000m));
        var session = await StartBalanceAsync(station, customer, 60);

        Advance(TimeSpan.FromMinutes(10));
        var stop = await SvcAsync(s => s.StopAsync(session.Id));

        Assert.True(stop.Success, stop.Error);
        Assert.Equal(0m, stop.Value);
        await using var db = await DbAsync();
        var refund = await db.CustomerMinuteLedger.SingleAsync(l => l.Reason == LedgerReason.SessionRefund);
        Assert.Equal(50, refund.MinutesDelta);
        Assert.Null(refund.StationTypeId);
        Assert.Equal(110, await BalanceAsync(customer));
        var ended = await LoadAsync(session.Id);
        Assert.Equal(SessionStatus.Ended, ended.Status);
        Assert.Equal(SessionEndReason.Stopped, ended.EndReason);
    }

    [Fact]
    public async Task Type_bucket_is_debited_first_and_refunded_last_drawn_first()
    {
        var typeId = await TypeAsync("Standard");
        var station = await StationAsync("PC-01", typeId);
        var customer = await CustomerAsync("Alice");
        await SellAsync(customer, await PackageAsync("Standard 30", 30, 30000m, typeId));
        await SellAsync(customer, await PackageAsync("Any 100", 100, 90000m));

        var session = await StartBalanceAsync(station, customer, 50);

        await using (var db = await DbAsync())
        {
            var debits = await db.CustomerMinuteLedger.Where(l => l.Reason == LedgerReason.SessionDebit).OrderBy(l => l.Id).ToListAsync();
            Assert.Equal([(typeId, -30), ((int?)null, -20)], debits.Select(d => (d.StationTypeId, d.MinutesDelta)).ToArray());
        }

        var stop = await SvcAsync(s => s.StopAsync(session.Id));
        Assert.True(stop.Success, stop.Error);

        await using (var db = await DbAsync())
        {
            var refunds = await db.CustomerMinuteLedger.Where(l => l.Reason == LedgerReason.SessionRefund).OrderBy(l => l.Id).ToListAsync();
            Assert.Equal([((int?)null, 20), (typeId, 30)], refunds.Select(r => (r.StationTypeId, r.MinutesDelta)).ToArray());
        }

        Assert.Equal(30, await BalanceAsync(customer, typeId));
        Assert.Equal(100, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Insufficient_balance_fails_and_writes_nothing()
    {
        var station = await StationAsync();
        var customer = await CustomerAsync("Alice");
        await SellAsync(customer, await PackageAsync("1 hour", 60, 50000m));

        var result = await TryStartBalanceAsync(station, customer, 61);

        Assert.False(result.Success);
        Assert.Contains("60", result.Error);
        await using var db = await DbAsync();
        Assert.Empty(await db.Sessions.ToListAsync());
        Assert.Equal(1, await db.CustomerMinuteLedger.CountAsync());
    }

    [Fact]
    public async Task Sale_rolls_back_atomically_when_the_save_fails()
    {
        var customer = await CustomerAsync("Alice");
        var package = await PackageAsync("1 hour", 60, 50000m);
        var failing = new PackageService(new FailingSaveDbContextFactory(Factory.ConnectionString), Factory.Clock, new BalanceLock());

        await Assert.ThrowsAsync<DbUpdateException>(() => failing.SellAsync(customer, package, PaymentMethod.Card));

        await using var db = await DbAsync();
        Assert.Empty(await db.PackageSales.ToListAsync());
        Assert.Empty(await db.Payments.ToListAsync());
        Assert.Empty(await db.CustomerMinuteLedger.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_balance_starts_cannot_overspend()
    {
        var a = await StationAsync("PC-01");
        var b = await StationAsync("PC-02");
        var customer = await CustomerAsync("Alice");
        await SellAsync(customer, await PackageAsync("1 hour", 60, 50000m));

        var results = await Task.WhenAll(
            Task.Run(() => TryStartBalanceAsync(a, customer, 40)),
            Task.Run(() => TryStartBalanceAsync(b, customer, 40)));

        Assert.Single(results, r => r.Success);
        Assert.True(await BalanceAsync(customer) >= 0);
        Assert.Equal(20, await BalanceAsync(customer));
    }

    [Fact]
    public async Task Inactive_or_missing_customer_cannot_start_a_balance_session()
    {
        var station = await StationAsync();
        var customer = await CustomerAsync("Alice");
        await SellAsync(customer, await PackageAsync("1 hour", 60, 50000m));
        await using (var db = await DbAsync())
        {
            (await db.Customers.SingleAsync()).IsActive = false;
            await db.SaveChangesAsync();
        }

        var inactive = await TryStartBalanceAsync(station, customer, 10);
        var missing = await TryStartBalanceAsync(station, 9999, 10);

        Assert.False(inactive.Success);
        Assert.Equal(Cafe.Server.Stations.FailureKind.Validation, inactive.Kind);
        Assert.False(missing.Success);
        Assert.Equal(Cafe.Server.Stations.FailureKind.NotFound, missing.Kind);
    }
}
