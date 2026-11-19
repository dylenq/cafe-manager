using Cafe.Server.Data;
using Cafe.Server.Pricing;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Cafe.Shared;
using Cafe.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cafe.Tests;

public class SessionPricingTests : IDisposable
{
    private readonly CafeWebApplicationFactory _factory = new();

    public SessionPricingTests()
    {
        // FakeTimeProvider cannot go back, and the factory clock starts at the real instant, so move it
        // forward to the next 19:30 UTC. All rules here cover every day, so the date does not matter.
        // Cafe:TimeZone is UTC in the test factory.
        var now = _factory.Clock.GetUtcNow();
        var target = new DateTimeOffset(now.Year, now.Month, now.Day, 19, 30, 0, TimeSpan.Zero);
        if (target <= now)
        {
            target = target.AddDays(1);
        }

        _factory.Clock.SetUtcNow(target);
    }

    public void Dispose() => _factory.Dispose();

    private async Task<(int TypeId, int StationId)> SeedAsync(string typeName = "Standard")
    {
        using var scope = _factory.Services.CreateScope();
        var typeId = (await scope.ServiceProvider.GetRequiredService<StationTypeService>()
            .CreateAsync(new StationTypeInput { Name = typeName, HourlyRate = 6000m })).Value!.Id;
        var station = await scope.ServiceProvider.GetRequiredService<StationService>().CreateAsync(new StationInput
        {
            Name = "PC-01", StationTypeId = typeId, Kind = StationKind.PC,
        });
        Assert.True(station.Success, station.Error);
        return (typeId, station.Value!.Id);
    }

    private async Task<int> AddRuleAsync(RateRuleKind kind, decimal value, TimeOnly start, TimeOnly end, int? typeId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<RateRuleService>().CreateAsync(new RateRuleInput
        {
            Name = "Rule", StationTypeId = typeId, DaysOfWeek = DaysOfWeekFlags.All,
            StartTime = start, EndTime = end, Kind = kind, Value = value, Priority = 10,
        });
        Assert.True(result.Success, result.Error);
        return result.Value!.Id;
    }

    private async Task<decimal> QuoteAsync(int stationId)
    {
        using var scope = _factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<SessionService>().StartPrepaidAsync(stationId, 60);
        Assert.True(result.Success, result.Error);
        return result.Value!.ChargedAmount;
    }

    [Fact]
    public async Task Fixed_rule_window_splits_the_quote()
    {
        var (typeId, stationId) = await SeedAsync();
        await AddRuleAsync(RateRuleKind.FixedHourlyRate, 4000m, new TimeOnly(20, 0), new TimeOnly(22, 0), typeId);

        // 30 min at 6000/h plus 30 min at 4000/h.
        Assert.Equal(5000.00m, await QuoteAsync(stationId));
    }

    [Fact]
    public async Task Disabled_rule_is_ignored()
    {
        var (typeId, stationId) = await SeedAsync();
        var ruleId = await AddRuleAsync(RateRuleKind.FixedHourlyRate, 4000m, new TimeOnly(20, 0), new TimeOnly(22, 0), typeId);
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<RateRuleService>().SetEnabledAsync(ruleId, false);
        }

        Assert.Equal(6000.00m, await QuoteAsync(stationId));
    }

    [Fact]
    public async Task All_day_multiplier_scales_the_quote()
    {
        var (_, stationId) = await SeedAsync();
        await AddRuleAsync(RateRuleKind.Multiplier, 0.5m, new TimeOnly(0, 0), new TimeOnly(23, 59));

        Assert.Equal(3000.00m, await QuoteAsync(stationId));
    }

    [Fact]
    public async Task Rule_for_another_station_type_does_not_apply()
    {
        var (_, stationId) = await SeedAsync();
        int otherTypeId;
        using (var scope = _factory.Services.CreateScope())
        {
            otherTypeId = (await scope.ServiceProvider.GetRequiredService<StationTypeService>()
                .CreateAsync(new StationTypeInput { Name = "Premium", HourlyRate = 9000m })).Value!.Id;
        }

        await AddRuleAsync(RateRuleKind.FixedHourlyRate, 4000m, new TimeOnly(20, 0), new TimeOnly(22, 0), otherTypeId);

        Assert.Equal(6000.00m, await QuoteAsync(stationId));
    }
}
