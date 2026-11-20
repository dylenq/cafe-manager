using Cafe.Server.Customers;
using Cafe.Server.Data;
using Cafe.Server.Payments;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>D-06: package sales go through the payment and allocation path.</summary>
public class PackagePaymentTests : PosTestBase
{
    private Task<ServiceResult<SettleResult>> SellPaid(int customerId, int packageId, decimal due, params TenderLine[] tenders) =>
        Svc(sp => sp.GetRequiredService<PackageService>().SellPaidAsync(customerId, packageId, tenders, due));

    private async Task AssertNothingWrittenAsync()
    {
        await using var db = await DbAsync();
        Assert.Equal(0, await db.PackageSales.CountAsync());
        Assert.Equal(0, await db.Payments.CountAsync());
        Assert.Equal(0, await db.PaymentAllocations.CountAsync());
        Assert.Equal(0, await db.CustomerMinuteLedger.CountAsync());
    }

    [Fact]
    public async Task D06_single_cash_line_writes_sale_payment_allocation_and_ledger_credit()
    {
        var customerId = await CustomerAsync();
        var packageId = await PackageAsync("Hour", 60, 4000m);

        var result = await SellPaid(customerId, packageId, 4000m, new TenderLine(PaymentMethod.Cash, 4000m));

        Assert.True(result.Success, result.Error);
        Assert.Equal(0m, result.Value!.Change);
        await using var db = await DbAsync();
        var sale = await db.PackageSales.SingleAsync();
        var payment = await db.Payments.Include(p => p.Allocations).SingleAsync();
        Assert.Equal(sale.Id, payment.PackageSaleId);
        Assert.Equal(payment.Id, result.Value.ReceiptPaymentId);
        var allocation = Assert.Single(payment.Allocations);
        Assert.Equal((AllocationTargetType.PackageSale, sale.Id, 4000m), (allocation.TargetType, allocation.TargetId, allocation.Amount));
        Assert.Equal(60, await BalanceAsync(customerId));
        Assert.Equal(0m, OutstandingCalculator.Remaining(sale.Price, db.PaymentAllocations.AsEnumerable().Select(a => a.Amount)));
    }

    [Fact]
    public async Task D06_split_cash_and_card_writes_two_payments_one_ledger_credit_and_allocations_summing_to_the_price()
    {
        var customerId = await CustomerAsync();
        var packageId = await PackageAsync("Hour", 60, 4000m);

        var result = await SellPaid(customerId, packageId, 4000m,
            new TenderLine(PaymentMethod.Cash, 1500m), new TenderLine(PaymentMethod.Card, 2500m));

        Assert.True(result.Success, result.Error);
        await using var db = await DbAsync();
        var payments = await db.Payments.OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(2, payments.Count);
        Assert.Equal(payments[0].Id, payments[1].PrimaryPaymentId);
        Assert.Equal(4000m, (await db.PaymentAllocations.ToListAsync()).Sum(a => a.Amount));
        Assert.Equal(1, await db.CustomerMinuteLedger.CountAsync());
    }

    [Fact]
    public async Task D06_cash_tendered_above_the_price_returns_change_and_stores_the_price()
    {
        var customerId = await CustomerAsync();
        var packageId = await PackageAsync("Hour", 60, 4000m);

        var result = await SellPaid(customerId, packageId, 4000m, new TenderLine(PaymentMethod.Cash, 4000m, 5000m));

        Assert.True(result.Success, result.Error);
        Assert.Equal(1000m, result.Value!.Change);
        await using var db = await DbAsync();
        Assert.Equal(4000m, (await db.Payments.SingleAsync()).Amount);
    }

    [Fact]
    public async Task D06_underpayment_writes_nothing()
    {
        var customerId = await CustomerAsync();
        var packageId = await PackageAsync("Hour", 60, 4000m);

        var result = await SellPaid(customerId, packageId, 4000m, new TenderLine(PaymentMethod.Cash, 3900m));

        Assert.False(result.Success);
        Assert.Equal("A package must be paid in full.", result.Error);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task D06_overpayment_inactive_package_inactive_customer_and_changed_price_write_nothing()
    {
        var customerId = await CustomerAsync();
        var packageId = await PackageAsync("Hour", 60, 4000m);
        var offSale = await PackageAsync("Old", 30, 2000m);
        await Svc(sp => sp.GetRequiredService<PackageService>().SetActiveAsync(offSale, false));
        var gone = await CustomerAsync("Gone");
        await Svc(sp => sp.GetRequiredService<CustomerService>().SetActiveAsync(gone, false));

        Assert.False((await SellPaid(customerId, packageId, 4000m, new TenderLine(PaymentMethod.Card, 4500m))).Success);
        Assert.False((await SellPaid(customerId, offSale, 2000m, new TenderLine(PaymentMethod.Cash, 2000m))).Success);
        Assert.False((await SellPaid(gone, packageId, 4000m, new TenderLine(PaymentMethod.Cash, 4000m))).Success);
        var stale = await SellPaid(customerId, packageId, 3000m, new TenderLine(PaymentMethod.Cash, 3000m));
        Assert.Equal(SettlementService.ChangedMessage, stale.Error);

        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task D06_failed_save_leaves_no_sale_payment_allocation_or_ledger_rows()
    {
        var customerId = await CustomerAsync();
        var packageId = await PackageAsync("Hour", 60, 4000m);
        var failing = new PackageService(new FailingSaveDbContextFactory(Factory.ConnectionString), Factory.Clock, new BalanceLock());

        var result = await failing.SellPaidAsync(customerId, packageId, [new TenderLine(PaymentMethod.Cash, 4000m)], 4000m);

        Assert.False(result.Success);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task D06_phase5_sell_writes_one_payment_of_the_price_and_one_allocation()
    {
        var customerId = await CustomerAsync();
        var packageId = await PackageAsync("Hour", 60, 4000m);

        var sale = await SellAsync(customerId, packageId, PaymentMethod.Card);

        await using var db = await DbAsync();
        var payment = await db.Payments.Include(p => p.Allocations).SingleAsync();
        Assert.Equal(4000m, payment.Amount);
        Assert.Equal(PaymentMethod.Card, payment.Method);
        var allocation = Assert.Single(payment.Allocations);
        Assert.Equal((AllocationTargetType.PackageSale, sale.Id), (allocation.TargetType, allocation.TargetId));
    }

    [Fact]
    public async Task D06_phase5_sell_keeps_the_expected_price_check()
    {
        var customerId = await CustomerAsync();
        var packageId = await PackageAsync("Hour", 60, 4000m);

        var result = await Svc(sp => sp.GetRequiredService<PackageService>().SellAsync(customerId, packageId, PaymentMethod.Cash, 3000m));

        Assert.False(result.Success);
        Assert.Equal("Price changed — review the package and try again.", result.Error);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task D08_customer_receipts_include_their_package_sale()
    {
        var customerId = await CustomerAsync();
        await SellAsync(customerId, await PackageAsync("Hour", 60, 4000m));

        var receipts = await Svc(sp => sp.GetRequiredService<PaymentHistoryService>().ForCustomerAsync(customerId));

        var receipt = Assert.Single(receipts);
        Assert.Equal("Hour", receipt.Label);
    }
}
