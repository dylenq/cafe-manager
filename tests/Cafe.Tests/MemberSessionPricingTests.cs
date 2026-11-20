using Cafe.Server.Data;
using Cafe.Server.Pricing;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>PRIC-03 / CUST-06: member-snapshot session pricing and customer-linked starts. Type: 6000 base, 4000 member.</summary>
public class MemberSessionPricingTests : CustomerTestBase
{
    private async Task<int> MemberTypeStationAsync(string station = "PC-01")
    {
        var typeId = await TypeAsync("Standard", 6000m);
        await using (var db = await DbAsync())
        {
            var type = await db.StationTypes.SingleAsync(t => t.Id == typeId);
            type.MemberHourlyRate = 4000m;
            await db.SaveChangesAsync();
        }

        return await StationAsync(station, typeId);
    }

    private async Task SetMemberAsync(int customerId, bool member)
    {
        await using var db = await DbAsync();
        (await db.Customers.SingleAsync(c => c.Id == customerId)).IsMember = member;
        await db.SaveChangesAsync();
    }

    private Task<ServiceResult<Session>> StartPrepaid(int stationId, int minutes, int? customerId) =>
        SvcAsync(s => s.StartPrepaidAsync(stationId, minutes, customerId));

    [Fact]
    public async Task PRIC03_CUST06_prepaid_member_is_charged_member_rate_and_linked()
    {
        var station = await MemberTypeStationAsync();
        var customer = await CustomerAsync("Mia", member: true);

        var result = await StartPrepaid(station, 60, customer);

        Assert.True(result.Success, result.Error);
        var session = await LoadAsync(result.Value!.Id);
        Assert.Equal(4000m, session.ChargedAmount);
        Assert.Equal(customer, session.CustomerId);
        Assert.True(session.IsMemberPricing);
    }

    [Fact]
    public async Task PRIC03_guest_customer_and_no_customer_pay_base_rate()
    {
        var station = await MemberTypeStationAsync();
        var guest = await CustomerAsync("Gus", member: false);

        var linked = await StartPrepaid(station, 60, guest);
        Assert.True(linked.Success, linked.Error);
        var linkedSession = await LoadAsync(linked.Value!.Id);
        Assert.Equal(6000m, linkedSession.ChargedAmount);
        Assert.False(linkedSession.IsMemberPricing);
        Assert.Equal(guest, linkedSession.CustomerId);

        var other = await StationAsync("PC-02", await TypeAsync());
        var anonymous = await PrepaidAsync(other, 60);
        Assert.Equal(6000m, anonymous.ChargedAmount);
        Assert.Null(anonymous.CustomerId);
    }

    [Fact]
    public async Task PRIC03_T0508_extension_uses_the_start_snapshot_when_member_flag_is_flipped()
    {
        var station = await MemberTypeStationAsync();
        var customer = await CustomerAsync("Mia", member: true);
        var session = (await StartPrepaid(station, 60, customer)).Value!;

        await SetMemberAsync(customer, false);
        var quote = await SvcAsync(s => s.ExtendAsync(session.Id, 60));

        Assert.True(quote.Success, quote.Error);
        Assert.Equal(4000m, quote.Value);
        Assert.Equal(8000m, (await LoadAsync(session.Id)).ChargedAmount);
    }

    [Fact]
    public async Task PRIC03_T0508_guest_snapshot_stays_guest_after_customer_becomes_member()
    {
        var station = await MemberTypeStationAsync();
        var customer = await CustomerAsync("Gus", member: false);
        var session = (await StartPrepaid(station, 60, customer)).Value!;

        await SetMemberAsync(customer, true);
        var quote = await SvcAsync(s => s.ExtendAsync(session.Id, 60));

        Assert.Equal(6000m, quote.Value);
    }

    [Fact]
    public async Task PRIC03_open_ended_member_estimate_and_stop_charge_match_and_use_member_rate()
    {
        var station = await MemberTypeStationAsync();
        var customer = await CustomerAsync("Mia", member: true);
        var started = await SvcAsync(s => s.StartOpenEndedAsync(station, customer));
        Assert.True(started.Success, started.Error);

        Advance(TimeSpan.FromMinutes(30));
        await SetMemberAsync(customer, false);

        var card = (await Factory.Services.CreateScope().ServiceProvider.GetRequiredService<Cafe.Server.Dashboard.DashboardService>()
            .GetCardsAsync()).Single(c => c.StationId == station);
        var quote = await SvcAsync(s => s.GetStopQuoteAsync(started.Value!.Id));
        var stop = await SvcAsync(s => s.StopAsync(started.Value!.Id));

        Assert.Equal(2000m, quote.Value);
        Assert.Equal(2000m, stop.Value);
        Assert.Equal(2000m, card.Session!.ChargeAmount);
    }

    [Fact]
    public async Task PRIC03_open_ended_guest_pays_base_rate()
    {
        var station = await MemberTypeStationAsync();
        var guest = await CustomerAsync("Gus", member: false);
        var started = await SvcAsync(s => s.StartOpenEndedAsync(station, guest));
        Assert.True(started.Success, started.Error);

        Advance(TimeSpan.FromMinutes(30));
        var stop = await SvcAsync(s => s.StopAsync(started.Value!.Id));

        Assert.Equal(3000m, stop.Value);
    }

    [Fact]
    public async Task CUST06_T0507_inactive_or_missing_customer_is_rejected_and_no_session_created()
    {
        var station = await MemberTypeStationAsync();
        var customer = await CustomerAsync("Old", member: true);
        await using (var db = await DbAsync())
        {
            (await db.Customers.SingleAsync(c => c.Id == customer)).IsActive = false;
            await db.SaveChangesAsync();
        }

        var inactive = await StartPrepaid(station, 60, customer);
        var missing = await StartPrepaid(station, 60, 9999);
        var openInactive = await SvcAsync(s => s.StartOpenEndedAsync(station, customer));

        Assert.False(inactive.Success);
        Assert.False(missing.Success);
        Assert.Equal(FailureKind.NotFound, missing.Kind);
        Assert.False(openInactive.Success);
        await using var check = await DbAsync();
        Assert.Equal(0, await check.Sessions.CountAsync());
    }

    [Fact]
    public async Task PRIC03_members_only_rule_changes_member_charge_but_not_guest_charge()
    {
        var station = await MemberTypeStationAsync();
        var other = await StationAsync("PC-02", await TypeAsync());
        var member = await CustomerAsync("Mia", member: true);
        var guest = await CustomerAsync("Gus", member: false);
        await using (var db = await DbAsync())
        {
            db.RateRules.Add(new RateRule
            {
                Name = "Members happy hour", DaysOfWeek = DaysOfWeekFlags.All,
                StartTime = new TimeOnly(0, 0), EndTime = new TimeOnly(0, 0), Kind = RateRuleKind.FixedHourlyRate,
                Value = 2000m, Priority = 5, AppliesTo = RateRuleAudience.MembersOnly, IsEnabled = true,
            });
            await db.SaveChangesAsync();
        }

        var memberSession = await StartPrepaid(station, 60, member);
        var guestSession = await StartPrepaid(other, 60, guest);

        Assert.Equal(2000m, (await LoadAsync(memberSession.Value!.Id)).ChargedAmount);
        Assert.Equal(6000m, (await LoadAsync(guestSession.Value!.Id)).ChargedAmount);
    }
}
