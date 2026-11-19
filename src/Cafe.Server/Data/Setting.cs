using System.ComponentModel.DataAnnotations;

namespace Cafe.Server.Data;

public class Setting
{
    [Key]
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
