using Cafe.Server.Data;
using Cafe.Shared;

namespace Cafe.Server.Dashboard;

public sealed record StationCardModel(
    int StationId,
    string Name,
    string TypeName,
    StationKind Kind,
    StationStatus Status,
    SessionCardModel? Session = null,
    bool AgentOffline = false);

/// <summary>Snapshot of a non-ended session. Live values are derived from these plus the current time.</summary>
public sealed record SessionCardModel(
    int SessionId,
    SessionMode Mode,
    SessionStatus Status,
    int? PurchasedMinutes,
    long ClosedActiveSeconds,
    DateTimeOffset? OpenSegmentStartUtc,
    DateTimeOffset? GraceStartedAtUtc,
    int GraceMinutes,
    int WarningWindowSeconds,
    decimal ChargeAmount,
    DateTimeOffset SnapshotUtc);

/// <summary>An enabled station with no session, offered as a Move target.</summary>
public sealed record IdleStation(int Id, string Name);
