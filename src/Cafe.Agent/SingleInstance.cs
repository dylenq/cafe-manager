using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Cafe.Agent;

/// <summary>
/// Named global mutex so only one agent runs per PC, plus a ping/pong pair of named events so a second instance can
/// tell whether the first one is really alive. A mutex pre-created by another process (or left by a hung agent) must
/// not leave the PC without a lock screen: the second instance exits only when the first answers within a timeout,
/// otherwise it takes over and shows its own lock screen.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Global\CafeManagerAgent";
    public const string PingName = @"Global\CafeManagerAgent.Ping";
    public const string PongName = @"Global\CafeManagerAgent.Pong";

    private readonly Mutex? _mutex;
    private EventWaitHandle? _ping;
    private EventWaitHandle? _pong;
    private RegisteredWaitHandle? _registration;

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
            // The mutex exists and is owned by another account: we cannot tell whether it is a real agent.
            return new SingleInstance(null, false);
        }
    }

    /// <summary>
    /// First instance only: answers pings from a second instance while <paramref name="isHealthy"/> (a check that the
    /// UI thread is responsive) returns true.
    /// </summary>
    public void StartResponder(Func<bool> isHealthy)
    {
        try
        {
            _ping = new EventWaitHandle(false, EventResetMode.AutoReset, PingName);
            _pong = new EventWaitHandle(false, EventResetMode.AutoReset, PongName);
            _registration = ThreadPool.RegisterWaitForSingleObject(_ping, (_, _) =>
            {
                try
                {
                    if (isHealthy())
                    {
                        _pong.Set();
                    }
                }
                catch (Exception)
                {
                    // No answer means the other instance takes over, which is the safe outcome.
                }
            }, null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception)
        {
            // Without a responder a second instance simply takes over after its timeout.
        }
    }

    /// <summary>Second instance: true when the running agent answered within the timeout (it is alive and responsive).</summary>
    public static bool PingExisting(TimeSpan timeout)
    {
        try
        {
            using var ping = EventWaitHandle.OpenExisting(PingName);
            using var pong = EventWaitHandle.OpenExisting(PongName);
            pong.Reset();
            ping.Set();
            return pong.WaitOne(timeout);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _ping?.Dispose();
        _pong?.Dispose();
        _mutex?.Dispose();
    }
}
