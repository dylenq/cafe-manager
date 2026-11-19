using Cafe.Agent.Core;

namespace Cafe.Tests;

public sealed class EmergencyAttemptTrackerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("emergency").FullName;
    private DateTimeOffset _now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string StatePath => Path.Combine(_dir, "agent", EmergencyAttemptTracker.FileName);

    private EmergencyAttemptTracker Create() => new(StatePath, () => _now);

    private static void Fail(EmergencyAttemptTracker tracker, int times)
    {
        for (var i = 0; i < times; i++)
        {
            tracker.RecordFailure();
        }
    }

    [Fact]
    public void Three_failures_start_a_lockout_that_expires()
    {
        var tracker = Create();
        Fail(tracker, 2);
        Assert.Equal(TimeSpan.Zero, tracker.RemainingLockout);

        tracker.RecordFailure();
        Assert.Equal(EmergencyAttemptTracker.BaseLockout, tracker.RemainingLockout);

        _now += EmergencyAttemptTracker.BaseLockout + TimeSpan.FromSeconds(1);
        Assert.Equal(TimeSpan.Zero, tracker.RemainingLockout);
    }

    [Fact]
    public void Lockout_survives_a_restart_and_cannot_be_skipped_by_relaunching()
    {
        Fail(Create(), 3);

        var relaunched = Create();
        Assert.True(relaunched.RemainingLockout > TimeSpan.Zero);
    }

    [Fact]
    public void Failure_counter_survives_a_restart()
    {
        Fail(Create(), 2);

        var relaunched = Create();
        relaunched.RecordFailure();
        Assert.True(relaunched.RemainingLockout > TimeSpan.Zero);
    }

    [Fact]
    public void Lockout_escalates_and_is_capped()
    {
        var tracker = Create();
        Fail(tracker, 3);
        _now += TimeSpan.FromMinutes(20);
        Fail(tracker, 3);
        Assert.Equal(TimeSpan.FromSeconds(60), tracker.RemainingLockout);
        Assert.Equal(EmergencyAttemptTracker.MaxLockout, EmergencyAttemptTracker.LockoutFor(50));
    }

    [Fact]
    public void Success_resets_the_state_on_disk()
    {
        var tracker = Create();
        Fail(tracker, 3);
        _now += TimeSpan.FromMinutes(20);
        tracker.Reset();

        Assert.Equal(TimeSpan.Zero, Create().RemainingLockout);
        Fail(Create(), 3);
        Assert.Equal(EmergencyAttemptTracker.BaseLockout, Create().RemainingLockout);
    }

    [Fact]
    public void Corrupt_state_file_fails_secure_with_a_lockout()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, "{not json");

        Assert.True(Create().RemainingLockout > TimeSpan.Zero);
    }

    [Fact]
    public void Failed_attempts_are_logged_without_the_code()
    {
        var logger = new CapturingLogger();
        var tracker = new EmergencyAttemptTracker(StatePath, () => _now, logger);
        tracker.RecordFailure();

        Assert.Contains(logger.Messages, m => m.Contains("wrong code"));
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
