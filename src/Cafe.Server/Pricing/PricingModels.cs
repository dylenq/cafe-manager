using Cafe.Server.Data;

namespace Cafe.Server.Pricing;

/// <summary>A span of active time on one station type, priced at that type's base hourly rate.</summary>
public sealed record PricedInterval(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int StationTypeId, decimal BaseHourlyRate);

public sealed record PricingRequest(IReadOnlyList<PricedInterval> Intervals, IReadOnlyList<RateRule> Rules, TimeZoneInfo TimeZone);

/// <summary>A contiguous run billed at one effective hourly rate. Amount is unrounded; only the total is rounded.</summary>
public sealed record PricingWindow(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int StationTypeId,
    decimal HourlyRate,
    int? RuleId,
    long Seconds,
    decimal Amount);

public sealed record PricingResult(decimal Total, IReadOnlyList<PricingWindow> Windows);
