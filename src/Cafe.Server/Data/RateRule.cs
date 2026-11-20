namespace Cafe.Server.Data;

public class RateRule
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Null means the rule applies to all station types.</summary>
    public int? StationTypeId { get; set; }

    public StationType? StationType { get; set; }

    public DaysOfWeekFlags DaysOfWeek { get; set; }

    /// <summary>Local (cafe time zone) window start, inclusive.</summary>
    public TimeOnly StartTime { get; set; }

    /// <summary>Local window end, exclusive. EndTime less than or equal to StartTime crosses midnight.</summary>
    public TimeOnly EndTime { get; set; }

    public RateRuleKind Kind { get; set; }

    public decimal Value { get; set; }

    /// <summary>Higher wins when windows overlap; ties go to the lowest Id.</summary>
    public int Priority { get; set; }

    /// <summary>Who the rule applies to.</summary>
    public RateRuleAudience AppliesTo { get; set; }

    public bool IsEnabled { get; set; } = true;
}
