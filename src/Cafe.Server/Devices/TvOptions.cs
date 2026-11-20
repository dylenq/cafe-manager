namespace Cafe.Server.Devices;

/// <summary>Configuration section "Tv". Delays use real Task.Delay, so tests set the millisecond options to 0.</summary>
public sealed class TvOptions
{
    public const string SectionName = "Tv";

    public int RemotePort { get; set; } = 8002;

    public int RestPort { get; set; } = 8001;

    public bool UseTls { get; set; } = true;

    public int PollSeconds { get; set; } = 15;

    public int RestTimeoutSeconds { get; set; } = 2;

    public int ConnectTimeoutSeconds { get; set; } = 3;

    public int PairTimeoutSeconds { get; set; } = 30;

    /// <summary>Wait between sending the power key and re-reading the TV state.</summary>
    public int SettleMilliseconds { get; set; } = 3000;

    /// <summary>Wait between sending the power key and closing the channel, so the TV reads the frame.</summary>
    public int KeyFlushMilliseconds { get; set; } = 300;

    /// <summary>How long to watch the TV after the Wake-on-LAN packets. A TV can report standby for several seconds while it boots.</summary>
    public int WakeWaitMilliseconds { get; set; } = 8000;

    /// <summary>Gap between state reads inside the wake window. 0 or less means a single read at the end.</summary>
    public int WakePollMilliseconds { get; set; } = 1000;

    public int WolRounds { get; set; } = 3;

    public int WolRoundDelayMilliseconds { get; set; } = 200;

    public bool RunMonitor { get; set; } = true;

    /// <summary>A queued automatic TV action is cancelled after this long.</summary>
    public int ActionTimeoutSeconds { get; set; } = 30;

    public bool PowerOffOnStop { get; set; } = true;

    /// <summary>
    /// Hard time budget for the power-off at session expiry. Must stay below the expiry evaluator's per-handler timeout
    /// (10 s), otherwise a slow TV is reported as a generic handler timeout. The driver's worst case is longer (about
    /// 13 s with the default timings), so a TV that is still busy when this runs out counts as a failed attempt and is
    /// retried: the retry reads Standby and ends as AlreadyOff, so the power key is never sent twice.
    /// </summary>
    public int ExpiryActionTimeoutSeconds { get; set; } = 8;
}
