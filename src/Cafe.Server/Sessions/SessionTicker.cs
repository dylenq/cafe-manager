namespace Cafe.Server.Sessions;

/// <summary>Drives the expiry evaluator once at startup (restart recovery) and then every second on the injected clock.</summary>
public class SessionTicker(IExpiryEvaluator evaluator, TimeProvider timeProvider, ILogger<SessionTicker> logger) : BackgroundService
{
    public const string RunTickerKey = "Sessions:RunTicker";

    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue(RunTickerKey, true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        try
        {
            await EvaluateSafelyAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await EvaluateSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task EvaluateSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await evaluator.EvaluateAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Session expiry evaluation failed; will retry on the next tick.");
        }
    }
}
