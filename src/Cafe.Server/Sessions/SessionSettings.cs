namespace Cafe.Server.Sessions;

public sealed record SessionSettings(int GracePeriodMinutes, IReadOnlyList<int> WarningMinutes)
{
    public const int DefaultGraceMinutes = 2;

    public static readonly IReadOnlyList<int> DefaultWarningMinutes = [10, 5, 1];

    public static SessionSettings Defaults { get; } = new(DefaultGraceMinutes, DefaultWarningMinutes);

    /// <summary>The largest warning point: the start of the warning window.</summary>
    public int MaxWarningMinutes => WarningMinutes.Count == 0 ? 0 : WarningMinutes.Max();
}
