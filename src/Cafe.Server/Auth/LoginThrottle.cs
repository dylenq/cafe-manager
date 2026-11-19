namespace Cafe.Server.Auth;

/// <summary>
/// In-memory brute-force brake for the shared admin login. After <see cref="MaxFailures"/> failed attempts from one
/// client key within <see cref="Window"/>, further attempts are refused for <see cref="Lockout"/>. State is lost on
/// restart, which is acceptable for a single-server LAN app.
/// </summary>
public class LoginThrottle(TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);

    private sealed class Entry
    {
        public DateTimeOffset WindowStart;
        public int Failures;
        public DateTimeOffset? LockedUntil;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new();

    /// <summary>True (with the time left) while <paramref name="key"/> is locked out.</summary>
    public bool IsLockedOut(string key, out TimeSpan remaining)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            Prune(now);
            if (_entries.TryGetValue(key, out var entry) && entry.LockedUntil is { } until && until > now)
            {
                remaining = until - now;
                return true;
            }
        }

        remaining = TimeSpan.Zero;
        return false;
    }

    public void RecordFailure(string key)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            Prune(now);
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry { WindowStart = now };
                _entries[key] = entry;
            }

            entry.Failures++;
            if (entry.Failures >= MaxFailures)
            {
                entry.LockedUntil = now + Lockout;
            }
        }
    }

    public void Reset(string key)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }
    }

    // Drops entries whose failure window and lockout have both elapsed (also bounds memory use).
    private void Prune(DateTimeOffset now)
    {
        List<string>? expired = null;
        foreach (var (key, entry) in _entries)
        {
            var lockActive = entry.LockedUntil is { } until && until > now;
            if (!lockActive && now - entry.WindowStart >= Window)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired is not null)
        {
            foreach (var key in expired)
            {
                _entries.Remove(key);
            }
        }
    }
}
