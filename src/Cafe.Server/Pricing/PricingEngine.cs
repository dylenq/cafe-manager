using Cafe.Server.Data;

namespace Cafe.Server.Pricing;

/// <summary>
/// Pure, stateless pricing. No database, clock or logging access. Billing is by elapsed seconds, the session
/// total is rounded up to a whole minute of time and then to 2 decimals (AwayFromZero).
/// </summary>
public static class PricingEngine
{
    private const long SecondsPerMinute = 60;
    private const long TransitionProbeSeconds = 12 * 3600;

    private readonly record struct Span(long Start, long End, int TypeId, decimal BaseRate);

    public static PricingResult Price(PricingRequest request)
    {
        var tz = request.TimeZone;
        var rules = request.Rules.Where(r => r.IsEnabled).ToList();

        // Whole-second epoch spans; zero-length and negative spans are ignored.
        var spans = new List<Span>();
        foreach (var i in request.Intervals)
        {
            var s = i.StartUtc.ToUnixTimeSeconds();
            var e = i.EndUtc.ToUnixTimeSeconds();
            if (e > s)
            {
                spans.Add(new Span(s, e, i.StationTypeId, i.BaseHourlyRate));
            }
        }

        if (spans.Count == 0)
        {
            return new PricingResult(0m, []);
        }

        // Bill up to the next whole minute; the padding lands on the last interval.
        var total = spans.Sum(x => x.End - x.Start);
        var remainder = total % SecondsPerMinute;
        if (remainder != 0)
        {
            var last = spans[^1];
            spans[^1] = last with { End = last.End + (SecondsPerMinute - remainder) };
        }

        var windows = new List<PricingWindow>();
        foreach (var span in spans)
        {
            var applicable = rules
                .Where(r => (r.StationTypeId is null || r.StationTypeId == span.TypeId) && AudienceMatches(r, request.IsMember))
                .ToList();
            var cuts = CutPoints(span, applicable, tz);
            for (var k = 0; k < cuts.Count - 1; k++)
            {
                var from = cuts[k];
                var to = cuts[k + 1];
                var rule = Match(applicable, from, tz);
                var rate = rule is null
                    ? span.BaseRate
                    : rule.Kind == RateRuleKind.Multiplier ? span.BaseRate * rule.Value : rule.Value;
                AddSlice(windows, from, to, span.TypeId, rate, rule?.Id);
            }
        }

        var sum = windows.Sum(w => w.Amount);
        return new PricingResult(Math.Round(sum, 2, MidpointRounding.AwayFromZero), windows);
    }

    /// <summary>Base hourly rate for the audience: a member pays the member rate when set, a guest always the base rate.</summary>
    public static decimal BaseRate(StationType type, bool isMember) =>
        isMember ? (type.MemberHourlyRate ?? type.HourlyRate) : type.HourlyRate;

    private static bool AudienceMatches(RateRule r, bool isMember) => r.AppliesTo switch
    {
        RateRuleAudience.MembersOnly => isMember,
        RateRuleAudience.GuestsOnly => !isMember,
        _ => true,
    };

    private static void AddSlice(List<PricingWindow> windows, long from, long to, int typeId, decimal rate, int? ruleId)
    {
        var seconds = to - from;
        var amount = rate * seconds / 3600m;
        var fromUtc = DateTimeOffset.FromUnixTimeSeconds(from);
        var toUtc = DateTimeOffset.FromUnixTimeSeconds(to);
        if (windows.Count > 0)
        {
            var prev = windows[^1];
            if (prev.EndUtc == fromUtc && prev.StationTypeId == typeId && prev.HourlyRate == rate && prev.RuleId == ruleId)
            {
                windows[^1] = prev with { EndUtc = toUtc, Seconds = prev.Seconds + seconds, Amount = prev.Amount + amount };
                return;
            }
        }

        windows.Add(new PricingWindow(fromUtc, toUtc, typeId, rate, ruleId, seconds, amount));
    }

    private static DateTime LocalOf(long utcSeconds, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(utcSeconds).UtcDateTime, tz);

    /// <summary>Span endpoints plus every offset transition, local midnight and rule boundary strictly inside.</summary>
    private static List<long> CutPoints(Span span, List<RateRule> rules, TimeZoneInfo tz)
    {
        var cuts = new SortedSet<long> { span.Start, span.End };

        // DST transitions: sample and bisect, so elapsed-time billing never straddles a changed offset.
        var prev = span.Start;
        var prevOffset = OffsetAt(prev, tz);
        while (prev < span.End)
        {
            var next = Math.Min(prev + TransitionProbeSeconds, span.End);
            var nextOffset = OffsetAt(next, tz);
            if (nextOffset != prevOffset)
            {
                long lo = prev, hi = next;
                while (hi - lo > 1)
                {
                    var mid = lo + (hi - lo) / 2;
                    if (OffsetAt(mid, tz) == prevOffset)
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                cuts.Add(hi);
            }

            prev = next;
            prevOffset = nextOffset;
        }

        var times = new List<TimeOnly> { TimeOnly.MinValue };
        foreach (var r in rules)
        {
            times.Add(r.StartTime);
            times.Add(r.EndTime);
        }

        var firstDate = LocalOf(span.Start, tz).Date.AddDays(-1);
        var lastDate = LocalOf(span.End, tz).Date.AddDays(1);
        for (var d = firstDate; d <= lastDate; d = d.AddDays(1))
        {
            foreach (var t in times.Distinct())
            {
                var naive = d + t.ToTimeSpan();
                foreach (var utc in ToUtcSeconds(naive, tz))
                {
                    if (utc > span.Start && utc < span.End)
                    {
                        cuts.Add(utc);
                    }
                }
            }
        }

        return cuts.ToList();
    }

    private static TimeSpan OffsetAt(long utcSeconds, TimeZoneInfo tz) =>
        tz.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(utcSeconds));

    /// <summary>
    /// UTC instants at which the local wall time occurs: none inside a DST gap (the transition itself is already a
    /// cut, which is the instant after the gap), two when ambiguous.
    /// </summary>
    private static IEnumerable<long> ToUtcSeconds(DateTime naive, TimeZoneInfo tz)
    {
        naive = DateTime.SpecifyKind(naive, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(naive))
        {
            yield break;
        }

        var offsets = tz.IsAmbiguousTime(naive)
            ? tz.GetAmbiguousTimeOffsets(naive)
            : [tz.GetUtcOffset(naive)];
        foreach (var off in offsets)
        {
            yield return new DateTimeOffset(naive - off, TimeSpan.Zero).ToUnixTimeSeconds();
        }
    }

    private static RateRule? Match(List<RateRule> rules, long utcSeconds, TimeZoneInfo tz)
    {
        if (rules.Count == 0)
        {
            return null;
        }

        var local = LocalOf(utcSeconds, tz);
        var tod = TimeOnly.FromDateTime(local);
        var today = local.DayOfWeek.ToFlag();
        var yesterday = local.AddDays(-1).DayOfWeek.ToFlag();

        RateRule? best = null;
        foreach (var r in rules)
        {
            if (!Matches(r, tod, today, yesterday))
            {
                continue;
            }

            if (best is null || r.Priority > best.Priority || (r.Priority == best.Priority && r.Id < best.Id))
            {
                best = r;
            }
        }

        return best;
    }

    private static bool Matches(RateRule r, TimeOnly tod, DaysOfWeekFlags today, DaysOfWeekFlags yesterday)
    {
        if (r.EndTime > r.StartTime)
        {
            return r.DaysOfWeek.HasFlag(today) && tod >= r.StartTime && tod < r.EndTime;
        }

        // Crosses midnight: the day flags belong to the day the window starts.
        return (r.DaysOfWeek.HasFlag(today) && tod >= r.StartTime)
               || (r.DaysOfWeek.HasFlag(yesterday) && tod < r.EndTime);
    }
}
