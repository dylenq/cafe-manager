namespace Cafe.Tests.Infrastructure;

/// <summary>
/// A clock that only moves when code waits on it: every timer jumps the clock forward by its due time and then fires.
/// Code that polls "until N ms have passed" therefore runs the same number of iterations however loaded the machine is.
/// Periodic timers are not supported (nothing that uses this clock needs them).
/// </summary>
public sealed class SteppingTimeProvider : TimeProvider
{
    private long _ticks;

    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + Elapsed;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero)
        {
            throw new NotSupportedException("SteppingTimeProvider does not support periodic timers.");
        }

        var timer = new OneShot();
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            Interlocked.Add(ref _ticks, Math.Max(0, dueTime.Ticks));
            // Fire off the caller's stack: Task.Delay stores the timer after CreateTimer returns.
            ThreadPool.UnsafeQueueUserWorkItem(_ => { if (!timer.Disposed) callback(state); }, null);
        }

        return timer;
    }

    private sealed class OneShot : ITimer
    {
        public volatile bool Disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
