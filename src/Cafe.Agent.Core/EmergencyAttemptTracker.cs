using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Cafe.Agent.Core;

/// <summary>
/// Counts wrong emergency unlock codes and imposes an escalating lockout (30 s, 60 s, 2 min ... capped at 15 min).
/// The counter and the lockout end are persisted to a small file (default %ProgramData%\CafeManager\agent) so killing
/// and relaunching the agent does not reset them. The file's ACL (administrators only) is set by the installer
/// (Phase 7). An unreadable or corrupt file fails secure: a full lockout starts. Every failed attempt is logged
/// (never the code).
/// </summary>
public sealed class EmergencyAttemptTracker
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan BaseLockout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(15);
    public const string FileName = "emergency-lockout.json";

    private sealed class State
    {
        public int Failures { get; set; }

        public int Rounds { get; set; }

        public DateTimeOffset LockedUntilUtc { get; set; }
    }

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly ILogger? _logger;
    private State _state = new();

    public EmergencyAttemptTracker(string? statePath = null, Func<DateTimeOffset>? now = null, ILogger? logger = null)
    {
        _path = statePath;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _logger = logger;
        Load();
    }

    /// <summary>The default state file: %ProgramData%\CafeManager\agent\emergency-lockout.json.</summary>
    public static string DefaultStatePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CafeManager", "agent", FileName);

    public static EmergencyAttemptTracker CreateDefault(ILogger? logger = null) => new(DefaultStatePath(), null, logger);

    public static TimeSpan LockoutFor(int round)
    {
        var seconds = BaseLockout.TotalSeconds * Math.Pow(2, Math.Clamp(round, 0, 20));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxLockout.TotalSeconds));
    }

    public TimeSpan RemainingLockout
    {
        get
        {
            lock (_gate)
            {
                var left = _state.LockedUntilUtc - _now();
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _state.Failures++;
            _logger?.LogWarning("Emergency unlock: wrong code (failure {Count} of {Max}).", _state.Failures, MaxAttempts);
            if (_state.Failures >= MaxAttempts)
            {
                var lockout = LockoutFor(_state.Rounds);
                _state.Failures = 0;
                _state.Rounds++;
                _state.LockedUntilUtc = _now() + lockout;
                _logger?.LogWarning("Emergency unlock: too many wrong codes, locked out for {Seconds} s.", (int)lockout.TotalSeconds);
            }

            Save();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _state = new State();
            Save();
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? throw new JsonException("empty");
            _state.Failures = Math.Max(0, _state.Failures);
            _state.Rounds = Math.Max(0, _state.Rounds);
        }
        catch (Exception ex)
        {
            // Fail secure: a tampered or corrupt file must not hand out a fresh set of attempts.
            _logger?.LogWarning("Emergency lockout state unreadable ({Reason}); starting a lockout.", ex.GetType().Name);
            _state = new State { Rounds = 1, LockedUntilUtc = _now() + LockoutFor(1) };
            Save();
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Persisting is best effort; the in-memory lockout still applies.
            _logger?.LogWarning("Could not save the emergency lockout state ({Reason}).", ex.GetType().Name);
        }
    }
}
