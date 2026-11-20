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

    /// <summary>Wait after the Wake-on-LAN packets before looking at the TV again.</summary>
    public int WakeWaitMilliseconds { get; set; } = 4000;

    public int WolRounds { get; set; } = 3;

    public int WolRoundDelayMilliseconds { get; set; } = 200;

    public bool RunMonitor { get; set; } = true;

    public bool PowerOffOnStop { get; set; } = true;
}
