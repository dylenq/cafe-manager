using Cafe.Shared;

namespace Cafe.Server.Data;

public class Station
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int StationTypeId { get; set; }

    public StationType StationType { get; set; } = null!;

    public StationKind Kind { get; set; }

    /// <summary>Windows machine id of the PC agent (Kind = PC).</summary>
    public string? AgentMachineId { get; set; }

    /// <summary>Samsung TV address (Kind = Console).</summary>
    public string? TvIpAddress { get; set; }

    public string? TvMacAddress { get; set; }

    /// <summary>Set during TV pairing (Phase 4).</summary>
    public string? TvToken { get; set; }

    public int SortOrder { get; set; }

    public bool IsEnabled { get; set; } = true;
}
