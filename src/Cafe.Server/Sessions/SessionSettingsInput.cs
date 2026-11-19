using System.ComponentModel.DataAnnotations;

namespace Cafe.Server.Sessions;

public class SessionSettingsInput
{
    [Range(0, 30, ErrorMessage = "Grace period must be between 0 and 30 minutes.")]
    public int GracePeriodMinutes { get; set; } = SessionSettings.DefaultGraceMinutes;

    [Required(ErrorMessage = "Enter at least one warning time, for example 10,5,1.")]
    public string WarningMinutes { get; set; } = string.Join(",", SessionSettings.DefaultWarningMinutes);
}
