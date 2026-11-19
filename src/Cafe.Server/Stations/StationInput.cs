using System.ComponentModel.DataAnnotations;
using Cafe.Shared;

namespace Cafe.Server.Stations;

public class StationInput
{
    // Dotted-quad IPv4, each octet 0-255. Empty or whitespace-only is allowed (the field is optional).
    private const string Octet = "(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])";

    [Required(ErrorMessage = "Name is required.")]
    [StringLength(100, ErrorMessage = "Name must be 100 characters or fewer.")]
    public string Name { get; set; } = string.Empty;

    [Range(1, int.MaxValue, ErrorMessage = "Choose a station type.")]
    public int StationTypeId { get; set; }

    public StationKind Kind { get; set; }

    [StringLength(100, ErrorMessage = "Machine ID must be 100 characters or fewer.")]
    public string? AgentMachineId { get; set; }

    [RegularExpression(@"^\s*(" + Octet + @"(\." + Octet + @"){3})?\s*$", ErrorMessage = "Enter a valid IPv4 address.")]
    public string? TvIpAddress { get; set; }

    [RegularExpression(@"^\s*(([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2})?\s*$",
        ErrorMessage = "Enter a valid MAC address like AA:BB:CC:DD:EE:FF.")]
    public string? TvMacAddress { get; set; }

    public int SortOrder { get; set; }

    public bool IsEnabled { get; set; } = true;
}
