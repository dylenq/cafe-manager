using Cafe.Server.Customers;
using Cafe.Server.Data;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class PackageServiceTests : CustomerTestBase
{
    private Task<T> PkgAsync<T>(Func<PackageService, Task<T>> action) =>
        InScopeAsync(sp => action(sp.GetRequiredService<PackageService>()));

    private static TimePackageInput Input(string name = "2 hours", int minutes = 120, decimal price = 100m, int? typeId = null) =>
        new() { Name = name, Minutes = minutes, Price = price, StationTypeId = typeId };

    [Fact]
    public async Task CUST03_create_persists_and_trims_name()
    {
        var result = await PkgAsync(p => p.CreateAsync(Input("  Night owl  ", 300, 50.5m)));
        Assert.True(result.Success, result.Error);
        var saved = (await PkgAsync(p => p.GetAsync(result.Value!.Id)))!;
        Assert.Equal("Night owl", saved.Name);
        Assert.Equal(50.5m, saved.Price);
    }

    [Theory]
    [InlineData("2 hours")]
    [InlineData("2 HOURS")]
    [InlineData(" 2 hours ")]
    public async Task CUST03_duplicate_name_ignoring_case_is_a_Name_field_error(string dup)
    {
        await PackageAsync("2 hours", 120, 100m);
        var result = await PkgAsync(p => p.CreateAsync(Input(dup)));
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("Name"));
    }

    [Theory]
    [InlineData("", 60, 1, "Name")]
    [InlineData("   ", 60, 1, "Name")]
    [InlineData("X", 0, 1, "Minutes")]
    [InlineData("X", 10001, 1, "Minutes")]
    [InlineData("X", 60, -1, "Price")]
    public async Task CUST03_invalid_input_is_a_field_error(string name, int minutes, decimal price, string field)
    {
        var result = await PkgAsync(p => p.CreateAsync(Input(name, minutes, price)));
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey(field));
    }

    [Fact]
    public async Task CUST03_name_over_100_chars_is_rejected_and_boundary_values_pass()
    {
        Assert.False((await PkgAsync(p => p.CreateAsync(Input(new string('x', 101))))).Success);
        Assert.True((await PkgAsync(p => p.CreateAsync(Input(new string('x', 100), 1, 0m)))).Success);
        Assert.True((await PkgAsync(p => p.CreateAsync(Input("Max", 10000)))).Success);
    }

    [Fact]
    public async Task CUST03_unknown_station_type_is_a_StationTypeId_field_error()
    {
        var result = await PkgAsync(p => p.CreateAsync(Input(typeId: 9999)));
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("StationTypeId"));
        var type = await TypeAsync();
        Assert.True((await PkgAsync(p => p.CreateAsync(Input(typeId: type)))).Success);
    }

    [Fact]
    public async Task CUST03_list_orders_by_name_and_can_hide_inactive()
    {
        await PackageAsync("Beta", 60, 1m);
        var alpha = await PackageAsync("alpha", 60, 1m);
        await PkgAsync(p => p.SetActiveAsync(alpha, false));
        Assert.Equal(["alpha", "Beta"], (await PkgAsync(p => p.ListAsync())).Select(x => x.Name).ToList());
        Assert.Equal(["Beta"], (await PkgAsync(p => p.ListAsync(includeInactive: false))).Select(x => x.Name).ToList());
    }

    [Fact]
    public async Task CUST03_update_changes_fields_keeps_own_name_and_rejects_anothers()
    {
        var id = await PackageAsync("2 hours", 120, 100m);
        await PackageAsync("1 hour", 60, 60m);
        var ok = await PkgAsync(p => p.UpdateAsync(id, Input("2 HOURS", 150, 120m)));
        Assert.True(ok.Success, ok.Error);
        var saved = (await PkgAsync(p => p.GetAsync(id)))!;
        Assert.Equal((150, 120m), (saved.Minutes, saved.Price));

        var clash = await PkgAsync(p => p.UpdateAsync(id, Input("1 hour")));
        Assert.False(clash.Success);
        Assert.True(clash.FieldErrors.ContainsKey("Name"));
        Assert.Equal(FailureKind.NotFound, (await PkgAsync(p => p.UpdateAsync(9999, Input()))).Kind);
    }

    [Fact]
    public async Task CUST03_deactivated_package_cannot_be_sold_and_nothing_is_written()
    {
        var customer = await CustomerAsync();
        var package = await PackageAsync("2 hours", 120, 100m);
        await PkgAsync(p => p.SetActiveAsync(package, false));

        var result = await PkgAsync(p => p.SellAsync(customer, package, PaymentMethod.Cash));

        Assert.False(result.Success);
        Assert.Contains("no longer on sale", result.Error);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task CUST03_reactivated_package_can_be_sold_again()
    {
        var customer = await CustomerAsync();
        var package = await PackageAsync("2 hours", 120, 100m);
        await PkgAsync(p => p.SetActiveAsync(package, false));
        await PkgAsync(p => p.SetActiveAsync(package, true));
        await SellAsync(customer, package);
        Assert.Equal(120, await BalanceAsync(customer));
    }

    [Fact]
    public async Task CUST03_delete_removes_a_never_sold_package()
    {
        var id = await PackageAsync("2 hours", 120, 100m);
        Assert.True((await PkgAsync(p => p.DeleteAsync(id))).Success);
        Assert.Null(await PkgAsync(p => p.GetAsync(id)));
        Assert.Equal(FailureKind.NotFound, (await PkgAsync(p => p.DeleteAsync(id))).Kind);
    }

    [Fact]
    public async Task CUST03_delete_of_a_sold_package_says_deactivate_and_keeps_history_in_the_balance()
    {
        var customer = await CustomerAsync();
        var package = await PackageAsync("2 hours", 120, 100m);
        await SellAsync(customer, package);

        var delete = await PkgAsync(p => p.DeleteAsync(package));
        Assert.False(delete.Success);
        Assert.Contains("Deactivate", delete.Error);

        await PkgAsync(p => p.SetActiveAsync(package, false));
        await using var db = await DbAsync();
        Assert.Equal(1, await db.PackageSales.CountAsync());
        Assert.Equal(1, await db.Payments.CountAsync());
        Assert.Equal(1, await db.CustomerMinuteLedger.CountAsync());
        Assert.Equal(120, await BalanceAsync(customer));
    }

    [Fact]
    public async Task CUST04_free_package_still_writes_sale_payment_and_credit()
    {
        var customer = await CustomerAsync();
        var package = await PackageAsync("Welcome gift", 15, 0m);
        await SellAsync(customer, package);
        await using var db = await DbAsync();
        Assert.Equal(0m, (await db.Payments.SingleAsync()).Amount);
        Assert.Equal(1, await db.PackageSales.CountAsync());
        Assert.Equal(15, await BalanceAsync(customer));
    }

    [Fact]
    public async Task CUST04_sale_to_an_inactive_or_missing_customer_or_package_is_rejected_and_writes_nothing()
    {
        var customer = await CustomerAsync();
        var package = await PackageAsync("2 hours", 120, 100m);
        await InScopeAsync(sp => sp.GetRequiredService<CustomerService>().SetActiveAsync(customer, false));

        Assert.False((await PkgAsync(p => p.SellAsync(customer, package, PaymentMethod.Cash))).Success);
        Assert.Equal(FailureKind.NotFound, (await PkgAsync(p => p.SellAsync(9999, package, PaymentMethod.Cash))).Kind);
        var active = await CustomerAsync("Bob");
        Assert.Equal(FailureKind.NotFound, (await PkgAsync(p => p.SellAsync(active, 9999, PaymentMethod.Cash))).Kind);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task CUST04_sale_snapshots_price_minutes_and_bucket_so_later_edits_do_not_change_it()
    {
        var customer = await CustomerAsync();
        var package = await PackageAsync("2 hours", 120, 100m);
        var sale = await SellAsync(customer, package);

        var type = await TypeAsync();
        Assert.True((await PkgAsync(p => p.UpdateAsync(package, Input("2 hours", 30, 999m, type)))).Success);

        await using var db = await DbAsync();
        var saved = await db.PackageSales.SingleAsync(s => s.Id == sale.Id);
        Assert.Equal((120, 100m, (int?)null), (saved.Minutes, saved.Price, saved.StationTypeId));
        Assert.Equal(100m, (await db.Payments.SingleAsync()).Amount);
        Assert.Equal(120, await BalanceAsync(customer));
        Assert.Equal(0, await BalanceAsync(customer, type));
    }

    [Fact]
    public async Task CUST04_station_type_package_credits_the_type_bucket_and_unrestricted_credits_any_type()
    {
        var type = await TypeAsync();
        var customer = await CustomerAsync();
        await SellAsync(customer, await PackageAsync("PC hour", 60, 50m, type));
        await SellAsync(customer, await PackageAsync("Any hour", 45, 40m));

        Assert.Equal(60, await BalanceAsync(customer, type));
        Assert.Equal(45, await BalanceAsync(customer));
    }

    [Fact]
    public async Task CUST04_T0502_failed_save_rolls_back_sale_payment_and_ledger()
    {
        var customer = await CustomerAsync();
        var package = await PackageAsync("2 hours", 120, 100m);
        var failing = new PackageService(new FailingSaveDbContextFactory(Factory.ConnectionString), Factory.Clock, new BalanceLock());

        var result = await failing.SellAsync(customer, package, PaymentMethod.Cash);

        Assert.False(result.Success);
        await AssertNothingWrittenAsync();
    }

    private async Task AssertNothingWrittenAsync()
    {
        await using var db = await DbAsync();
        Assert.Equal(0, await db.PackageSales.CountAsync());
        Assert.Equal(0, await db.Payments.CountAsync());
        Assert.Equal(0, await db.CustomerMinuteLedger.CountAsync());
    }
}
