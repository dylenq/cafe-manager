namespace Cafe.Server.Sessions;

/// <summary>Fans signals out to every sink. A failing sink is logged and never affects the others or the caller.</summary>
public sealed class SessionSignalPublisher(IEnumerable<ISessionEventSink> sinks, ILogger<SessionSignalPublisher> logger)
{
    private readonly ISessionEventSink[] _sinks = sinks.ToArray();

    public async Task PublishAsync(IEnumerable<SessionSignal> signals, CancellationToken cancellationToken = default)
    {
        foreach (var signal in signals)
        {
            foreach (var sink in _sinks)
            {
                try
                {
                    await sink.OnSessionSignalAsync(signal, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Session signal sink {Sink} failed for {Kind} on station {StationId}.",
                        sink.GetType().Name, signal.Kind, signal.StationId);
                }
            }
        }
    }
}
