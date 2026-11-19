using Cafe.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Pricing;

/// <summary>Supplies the cafe time zone and the enabled rate rules to the pricing engine callers.</summary>
public class PricingConfigService(IConfiguration configuration, ILogger<PricingConfigService> logger)
{
    private readonly Lazy<TimeZoneInfo> _timeZone =
        new(() => CafeTimeZone.Resolve(configuration["Cafe:TimeZone"], logger));

    public TimeZoneInfo TimeZone => _timeZone.Value;

    public Task<List<RateRule>> GetEnabledRulesAsync(CafeDbContext db, CancellationToken cancellationToken = default) =>
        db.RateRules.AsNoTracking().Where(r => r.IsEnabled).OrderBy(r => r.Id).ToListAsync(cancellationToken);
}
