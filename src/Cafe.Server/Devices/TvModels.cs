namespace Cafe.Server.Devices;

public enum TvPowerState
{
    On,
    Standby,
    Unreachable,
    Unknown,
}

/// <summary>Result of pairing. <see cref="Token"/> is the secret for the TV remote channel; never log it.</summary>
public sealed record TvPairResult(bool Success, string? Token, string? Error);

public enum TvActionOutcome
{
    PoweredOff,
    AlreadyOff,
    Unreachable,
    KeySent,
    Failed,
    PoweredOn,
    AlreadyOn,
    WakeSent,
}

public sealed record TvActionResult(TvActionOutcome Outcome, TvPowerState State, string Message)
{
    /// <summary>True for every outcome except Failed and Unreachable.</summary>
    public bool IsSuccess => Outcome is not (TvActionOutcome.Failed or TvActionOutcome.Unreachable);
}
