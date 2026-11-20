using Cafe.Server.Customers;
using Cafe.Server.Data;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class BalanceServiceTests : CustomerTestBase
{
    private Task<T> BalAsync<T>(Func<BalanceService, Task<T>> action) =>
        InScopeAsync(sp => action(sp.GetRequiredService<BalanceService>()));

    private Task<ServiceResult<CustomerMinuteLedger>> AdjustAsync(int customer, int? type, int delta, string? note = "recount") =>
        BalAsync(b => b.AdjustAsync(customer, type, delta, note));

    private async Task CreditAsync(int customer, int minutes, int? typeId = null) =>
        await SellAsync(customer, await PackageAsync($"Pkg {Guid.NewGuid():N}", minutes, 10m, typeId));

    [Fact]
    public async Task CUST04_balance_lists_only_nonzero_buckets_any_type_first_with_total()
    {
        var pc = await TypeAsync("PC");
        var ps = await TypeAsync("PS5");
        var customer = await CustomerAsync();
        await CreditAsync(customer, 60, pc);
        await CreditAsync(customer, 30);
        await CreditAsync(customer, 20, ps);
        await AdjustAsync(customer, ps, -20);

        var summary = await BalAsync(b => b.GetBalanceAsync(customer));

        Assert.Equal(2, summary.Buckets.Count);
        Assert.Null(summary.Buckets[0].StationTypeId);
        Assert.Equal(30, summary.Buckets[0].Minutes);
        Assert.Equal(("PC", 60), (summary.Buckets[1].StationTypeName, summary.Buckets[1].Minutes));
        Assert.Equal(90, summary.TotalMinutes);
    }

    [Fact]
    public async Task CUST04_balance_of_unknown_customer_is_empty()
    {
        var summary = await BalAsync(b => b.GetBalanceAsync(9999));
        Assert.Empty(summary.Buckets);
        Assert.Equal(0, summary.TotalMinutes);
    }

    [Fact]
    public async Task CUST04_usable_minutes_is_type_bucket_plus_any_bucket()
    {
        var pc = await TypeAsync("PC");
        var ps = await TypeAsync("PS5");
        var customer = await CustomerAsync();
        await CreditAsync(customer, 60, pc);
        await CreditAsync(customer, 30);
        Assert.Equal(90, await BalAsync(b => b.GetUsableMinutesAsync(customer, pc)));
        Assert.Equal(30, await BalAsync(b => b.GetUsableMinutesAsync(customer, ps)));
    }

    [Fact]
    public async Task CUST04_a_negative_legacy_bucket_never_offsets_another()
    {
        var pc = await TypeAsync("PC");
        var customer = await CustomerAsync();
        await CreditAsync(customer, 50);
        await using (var db = await DbAsync())
        {
            db.CustomerMinuteLedger.Add(new CustomerMinuteLedger
            {
                CustomerId = customer, StationTypeId = pc, MinutesDelta = -30, Reason = LedgerReason.ManualAdjustment, AtUtc = Start,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(50, await BalAsync(b => b.GetUsableMinutesAsync(customer, pc)));
    }

    [Fact]
    public async Task CUST04_adjust_credit_writes_one_ManualAdjustment_row_with_note_and_time()
    {
        var customer = await CustomerAsync();
        var result = await AdjustAsync(customer, null, 25, "  free minutes for the bug  ");
        Assert.True(result.Success, result.Error);

        await using var db = await DbAsync();
        var row = await db.CustomerMinuteLedger.SingleAsync();
        Assert.Equal(LedgerReason.ManualAdjustment, row.Reason);
        Assert.Equal(25, row.MinutesDelta);
        Assert.Equal("free minutes for the bug", row.Note);
        Assert.Equal(Start, row.AtUtc);
        Assert.Null(row.StationTypeId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CUST04_adjust_requires_a_note(string? note)
    {
        var customer = await CustomerAsync();
        var result = await AdjustAsync(customer, null, 10, note);
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("Note"));
        await AssertNoLedgerAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100001)]
    [InlineData(-100001)]
    public async Task CUST04_adjust_rejects_zero_and_oversized_deltas(int delta)
    {
        var customer = await CustomerAsync();
        var result = await AdjustAsync(customer, null, delta);
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("MinutesDelta"));
        await AssertNoLedgerAsync();
    }

    [Fact]
    public async Task CUST04_T0506_adjust_cannot_drive_a_bucket_below_zero()
    {
        var customer = await CustomerAsync();
        await CreditAsync(customer, 40);

        var tooMuch = await AdjustAsync(customer, null, -41);
        Assert.False(tooMuch.Success);
        Assert.Contains("only 40 min", tooMuch.Error);
        Assert.Equal(40, await BalanceAsync(customer));

        Assert.True((await AdjustAsync(customer, null, -40)).Success);
        Assert.Equal(0, await BalanceAsync(customer));
        Assert.False((await AdjustAsync(customer, null, -1)).Success);
    }

    [Fact]
    public async Task CUST04_T0506_a_debit_from_an_empty_type_bucket_is_rejected_even_when_another_bucket_has_minutes()
    {
        var pc = await TypeAsync("PC");
        var customer = await CustomerAsync();
        await CreditAsync(customer, 60);
        var result = await AdjustAsync(customer, pc, -5);
        Assert.False(result.Success);
        Assert.Contains("only 0 min", result.Error);
    }

    [Fact]
    public async Task CUST04_adjust_unknown_customer_is_NotFound_and_unknown_type_is_a_field_error()
    {
        Assert.Equal(FailureKind.NotFound, (await AdjustAsync(9999, null, 5)).Kind);
        var customer = await CustomerAsync();
        var result = await AdjustAsync(customer, 9999, 5);
        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey("StationTypeId"));
    }

    [Fact]
    public async Task CUST04_an_inactive_customer_can_still_be_adjusted_as_a_staff_correction()
    {
        var customer = await CustomerAsync();
        await CreditAsync(customer, 30);
        await InScopeAsync(sp => sp.GetRequiredService<CustomerService>().SetActiveAsync(customer, false));

        var result = await AdjustAsync(customer, null, -10, "refund paid in cash");

        Assert.True(result.Success, result.Error);
        Assert.Equal(20, await BalanceAsync(customer));
    }

    [Fact]
    public async Task CUST04_T0506_an_adjustment_racing_a_balance_start_cannot_overdraw()
    {
        var station = await StationAsync();
        var customer = await CustomerAsync();
        await CreditAsync(customer, 60);

        var adjust = AdjustAsync(customer, null, -50);
        var start = TryStartBalanceAsync(station, customer, 40);
        var results = (await adjust).Success;
        var started = (await start).Success;

        Assert.True(results != started, "exactly one of the two may win");
        Assert.True(await BalanceAsync(customer) >= 0);
    }

    [Fact]
    public async Task CUST04_detail_collects_balance_sales_ledger_and_recent_sessions()
    {
        var station = await StationAsync("PC-07");
        var customer = await CustomerAsync("Alice", "5551234567", member: true);
        var package = await PackageAsync("2 hours", 120, 100m);
        await SellAsync(customer, package, PaymentMethod.Card);
        Advance(TimeSpan.FromMinutes(1));
        var session = await StartBalanceAsync(station, customer, 60);
        Advance(TimeSpan.FromMinutes(1));
        await AdjustAsync(customer, null, 5, "goodwill");

        var detail = (await InScopeAsync(sp => sp.GetRequiredService<CustomerService>().GetDetailAsync(customer))).Value!;

        Assert.Equal("Alice", detail.Customer.Name);
        Assert.Equal(65, detail.Balance.TotalMinutes);
        var sale = Assert.Single(detail.Sales);
        Assert.Equal(("2 hours", 100m, 120, PaymentMethod.Card), (sale.PackageName, sale.Price, sale.Minutes, sale.Method));
        Assert.Equal(3, detail.Ledger.Count);
        Assert.Equal(LedgerReason.ManualAdjustment, detail.Ledger[0].Reason);
        Assert.Equal("goodwill", detail.Ledger[0].Note);
        Assert.Equal("Manual adjustment", detail.Ledger[0].ReasonText);
        var debit = detail.Ledger.Single(l => l.Reason == LedgerReason.SessionDebit);
        Assert.Equal(session.Id, debit.SessionId);
        var recent = Assert.Single(detail.Sessions);
        Assert.Equal(("PC-07", SessionMode.Balance), (recent.StationName, recent.Mode));
    }

    [Fact]
    public async Task CUST04_detail_lists_sales_newest_first_and_caps_sessions_at_20_and_ledger_at_100()
    {
        var station = await StationAsync();
        var customer = await CustomerAsync();
        var package = await PackageAsync("Hour", 60, 10m);
        for (var i = 0; i < 2; i++)
        {
            Advance(TimeSpan.FromMinutes(1));
            await SellAsync(customer, package);
        }

        await using (var db = await DbAsync())
        {
            for (var i = 0; i < 110; i++)
            {
                db.CustomerMinuteLedger.Add(new CustomerMinuteLedger
                {
                    CustomerId = customer, MinutesDelta = 1, Reason = LedgerReason.ManualAdjustment, AtUtc = Start.AddHours(1 + i), Note = "bulk",
                });
            }

            for (var i = 0; i < 25; i++)
            {
                db.Sessions.Add(new Session
                {
                    StationId = station, CustomerId = customer, Mode = SessionMode.Balance, Status = SessionStatus.Ended,
                    StartedAtUtc = Start.AddHours(i), EndedAtUtc = Start.AddHours(i).AddMinutes(30), EndReason = SessionEndReason.Stopped,
                });
            }

            await db.SaveChangesAsync();
        }

        var detail = (await InScopeAsync(sp => sp.GetRequiredService<CustomerService>().GetDetailAsync(customer))).Value!;

        Assert.Equal(2, detail.Sales.Count);
        Assert.True(detail.Sales[0].AtUtc > detail.Sales[1].AtUtc);
        Assert.Equal(100, detail.Ledger.Count);
        Assert.Equal(20, detail.Sessions.Count);
        Assert.True(detail.Sessions[0].StartedAtUtc > detail.Sessions[19].StartedAtUtc);
        Assert.Equal(24, (detail.Sessions[0].StartedAtUtc - Start).TotalHours);
    }

    [Fact]
    public async Task CUST04_detail_of_a_missing_customer_is_NotFound()
    {
        var result = await InScopeAsync(sp => sp.GetRequiredService<CustomerService>().GetDetailAsync(9999));
        Assert.False(result.Success);
        Assert.Equal(FailureKind.NotFound, result.Kind);
    }

    private async Task AssertNoLedgerAsync()
    {
        await using var db = await DbAsync();
        Assert.Equal(0, await db.CustomerMinuteLedger.CountAsync());
    }
}
