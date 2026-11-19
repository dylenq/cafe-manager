using Cafe.Server.Data;

namespace Cafe.Server.Sessions;

/// <summary>Default handler: records that the expiry action fired. Applies to every station kind.</summary>
public class LoggingExpiryActionHandler(ILogger<LoggingExpiryActionHandler> logger) : IExpiryActionHandler
{
    public Task ExecuteAsync(Station station, CancellationToken cancellationToken)
    {
        logger.LogInformation("Expiry action for station {Station} ({Kind}).", station.Name, station.Kind);
        return Task.CompletedTask;
    }
}
