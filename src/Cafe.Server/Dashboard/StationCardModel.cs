using Cafe.Shared;

namespace Cafe.Server.Dashboard;

public sealed record StationCardModel(int StationId, string Name, string TypeName, StationKind Kind, StationStatus Status);
