namespace Cafe.Server.Sessions;

public enum SessionSignalKind
{
    Started,
    Paused,
    Resumed,
    Extended,
    Moved,
    Stopped,
    WarningReached,
    GraceStarted,
}

/// <summary>A session change published after commit. Optional fields are only set for the kinds that use them.</summary>
public sealed record SessionSignal(
    SessionSignalKind Kind,
    int StationId,
    int SessionId,
    int? FromStationId = null,
    int? Minutes = null,
    int? GraceSeconds = null,
    bool ExtendedFromGrace = false);

/// <summary>
/// Multicast seam for session signals: Phase 3 adds the PC agent sink, Phase 4 adds a TV sink. Sinks run after the
/// mutation committed and must never be relied on for correctness of the mutation.
/// </summary>
public interface ISessionEventSink
{
    Task OnSessionSignalAsync(SessionSignal signal, CancellationToken cancellationToken);
}
