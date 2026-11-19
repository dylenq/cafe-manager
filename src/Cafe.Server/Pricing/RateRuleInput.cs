using System.ComponentModel.DataAnnotations;
using Cafe.Server.Data;

namespace Cafe.Server.Pricing;

public class RateRuleInput
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(100, ErrorMessage = "Name must be 100 characters or fewer.")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Null applies the rule to all station types.</summary>
    public int? StationTypeId { get; set; }

    public DaysOfWeekFlags DaysOfWeek { get; set; } = DaysOfWeekFlags.All;

    public TimeOnly StartTime { get; set; } = new(18, 0);

    public TimeOnly EndTime { get; set; } = new(22, 0);

    public RateRuleKind Kind { get; set; } = RateRuleKind.FixedHourlyRate;

    public decimal Value { get; set; }

    [Range(0, 1000, ErrorMessage = "Priority must be between 0 and 1000.")]
    public int Priority { get; set; }

    public bool IsEnabled { get; set; } = true;
}
