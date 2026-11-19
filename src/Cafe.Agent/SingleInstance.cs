namespace Cafe.Agent;

/// <summary>Named global mutex so only one agent runs per PC.</summary>
internal sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Global\CafeManagerAgent";

    private readonly Mutex? _mutex;

    private SingleInstance(Mutex? mutex, bool isFirst)
    {
        _mutex = mutex;
        IsFirst = isFirst;
    }

    public bool IsFirst { get; }

    public static SingleInstance Acquire()
    {
        try
        {
            var mutex = new Mutex(false, MutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return new SingleInstance(null, false);
            }

            return new SingleInstance(mutex, true);
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists and is owned by another account: another agent is running.
            return new SingleInstance(null, false);
        }
    }

    public void Dispose() => _mutex?.Dispose();
}
