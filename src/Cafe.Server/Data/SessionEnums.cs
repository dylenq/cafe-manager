namespace Cafe.Server.Data;

public enum SessionMode
{
    Prepaid = 0,
    OpenEnded = 1,
}

/// <summary>Explicit values: the filtered unique index on Sessions depends on Ended = 3.</summary>
public enum SessionStatus
{
    Active = 0,
    Paused = 1,
    Grace = 2,
    Ended = 3,
}

public enum SessionEndReason
{
    Stopped = 0,
    Expired = 1,
}

public enum SessionEventType
{
    Started = 0,
    Paused = 1,
    Resumed = 2,
    Extended = 3,
    Moved = 4,
    Stopped = 5,
    WarningReached = 6,
    Expired = 7,
    GraceElapsed = 8,
    ExpiryActionFired = 9,
}

public enum RateRuleKind
{
    FixedHourlyRate = 0,
    Multiplier = 1,
}

[Flags]
public enum DaysOfWeekFlags
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    All = Monday | Tuesday | Wednesday | Thursday | Friday | Saturday | Sunday,
}

public static class DaysOfWeekFlagsExtensions
{
    public static DaysOfWeekFlags ToFlag(this DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => DaysOfWeekFlags.Monday,
        DayOfWeek.Tuesday => DaysOfWeekFlags.Tuesday,
        DayOfWeek.Wednesday => DaysOfWeekFlags.Wednesday,
        DayOfWeek.Thursday => DaysOfWeekFlags.Thursday,
        DayOfWeek.Friday => DaysOfWeekFlags.Friday,
        DayOfWeek.Saturday => DaysOfWeekFlags.Saturday,
        _ => DaysOfWeekFlags.Sunday,
    };
}
