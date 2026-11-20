using Cafe.Server.Data;
using Cafe.Server.Pricing;
using Cafe.Server.Stations;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

/// <summary>PRIC-03 admin inputs: rule audience and the optional member hourly rate.</summary>
public class MemberPricingPagesTests : IDisposable
{
    private readonly CafeWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static RateRuleInput RuleInput(RateRuleAudience? audience = null)
    {
        var input = new RateRuleInput
        {
            Name = "Members Night", DaysOfWeek = DaysOfWeekFlags.All, StartTime = new TimeOnly(18, 0),
            EndTime = new TimeOnly(20, 0), Kind = RateRuleKind.FixedHourlyRate, Value = 2000m,
        };
        if (audience is { } a)
        {
            input.AppliesTo = a;
        }

        return input;
    }

    [Fact]
    public async Task PRIC03_rule_without_audience_defaults_to_everyone()
    {
        using var scope = _factory.Services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<RateRuleService>().CreateAsync(RuleInput());

        Assert.True(created.Success, created.Error);
        Assert.Equal(RateRuleAudience.Everyone, created.Value!.AppliesTo);
    }

    [Fact]
    public async Task PRIC03_rule_audience_round_trips_on_create_and_update()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<RateRuleService>();
        var created = await service.CreateAsync(RuleInput(RateRuleAudience.MembersOnly));
        Assert.Equal(RateRuleAudience.MembersOnly, (await service.GetAsync(created.Value!.Id))!.AppliesTo);

        var update = RuleInput(RateRuleAudience.GuestsOnly);
        Assert.True((await service.UpdateAsync(created.Value.Id, update)).Success);
        Assert.Equal(RateRuleAudience.GuestsOnly, (await service.GetAsync(created.Value.Id))!.AppliesTo);
    }

    [Fact]
    public async Task PRIC03_undefined_audience_is_a_field_error()
    {
        using var scope = _factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<RateRuleService>().CreateAsync(RuleInput((RateRuleAudience)99));

        Assert.False(result.Success);
        Assert.True(result.FieldErrors.ContainsKey(nameof(RateRuleInput.AppliesTo)));
    }

    [Fact]
    public async Task PRIC03_member_rate_is_optional_validated_and_round_trips()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<StationTypeService>();

        var plain = await service.CreateAsync(new StationTypeInput { Name = "Plain", HourlyRate = 6000m });
        Assert.True(plain.Success, plain.Error);
        Assert.Null(plain.Value!.MemberHourlyRate);

        var withRate = await service.CreateAsync(new StationTypeInput { Name = "VIP", HourlyRate = 6000m, MemberHourlyRate = 4000m });
        Assert.Equal(4000m, (await service.GetAsync(withRate.Value!.Id))!.MemberHourlyRate);

        var cleared = await service.UpdateAsync(withRate.Value.Id, new StationTypeInput { Name = "VIP", HourlyRate = 6000m });
        Assert.True(cleared.Success, cleared.Error);
        Assert.Null((await service.GetAsync(withRate.Value.Id))!.MemberHourlyRate);

        var negative = await service.CreateAsync(new StationTypeInput { Name = "Bad", HourlyRate = 6000m, MemberHourlyRate = -1m });
        Assert.False(negative.Success);
        Assert.True(negative.FieldErrors.ContainsKey(nameof(StationTypeInput.MemberHourlyRate)));
    }

    [Fact]
    public async Task PRIC03_pages_show_audience_and_member_rate()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<RateRuleService>().CreateAsync(RuleInput(RateRuleAudience.MembersOnly));
            await scope.ServiceProvider.GetRequiredService<StationTypeService>()
                .CreateAsync(new StationTypeInput { Name = "VIP", HourlyRate = 6000m, MemberHourlyRate = 4000m });
            await scope.ServiceProvider.GetRequiredService<StationTypeService>()
                .CreateAsync(new StationTypeInput { Name = "Plain", HourlyRate = 5000m });
        }

        using var client = await _factory.CreateAuthenticatedClientAsync();
        var rules = await (await client.GetAsync("/rate-rules")).Content.ReadAsStringAsync();
        var types = await (await client.GetAsync("/station-types")).Content.ReadAsStringAsync();

        Assert.Contains("Members only", rules);
        Assert.Contains("Member rate", types);
        Assert.Contains("4,000.00", types);
        Assert.Contains("Same as base", types);
    }
}
