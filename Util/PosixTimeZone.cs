using System.Globalization;
using System.Text;

namespace BusyTag.Lib.Util;

/// <summary>
/// Builds a POSIX <c>TZ</c> string (e.g. <c>CET-1CEST,M3.5.0,M10.5.0/3</c>) from
/// the host's local time zone. The device firmware (v3.0+) accepts this via
/// <c>AT+TZ=</c> and feeds it to newlib's <c>tzset()</c> so its on-screen clock
/// tracks local time, including daylight-saving transitions, without a network
/// time-zone lookup.
/// </summary>
public static class PosixTimeZone
{
    public static string GetLocalPosixTz() => ToPosixTz(TimeZoneInfo.Local);

    /// <summary>
    /// Returns a POSIX TZ string for the supplied zone. On Windows the zone's
    /// adjustment rules are floating ("last Sunday of March") and convert directly.
    /// On Android/iOS/macOS, .NET expands IANA data into per-year fixed-date rules
    /// that POSIX cannot express, so the zone's actual offset changes during the
    /// current year are probed and generalized back to floating rules. Falls back
    /// to a plain UTC-offset string (no DST) using the offset in effect right now.
    /// </summary>
    public static string ToPosixTz(TimeZoneInfo tz)
    {
        if (tz.SupportsDaylightSavingTime)
        {
            var result = FromAdjustmentRule(tz) ?? FromProbedTransitions(tz, DateTime.UtcNow.Year);
            if (result is not null)
                return result;
        }

        // No DST, or rules we can't express. Use the offset in effect NOW (not
        // BaseUtcOffset, which is the winter offset): the clock is then correct
        // until the next DST change instead of wrong for the whole summer.
        return OffsetOnly(tz.GetUtcOffset(DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Converts the zone's current adjustment rule when its transitions are in
    /// floating (Mm.w.d) form. Returns null for fixed-date transitions — that is
    /// the normal shape on Unix-based platforms, handled by probing instead.
    /// </summary>
    private static string? FromAdjustmentRule(TimeZoneInfo tz)
    {
        var rule = FindCurrentRule(tz);
        if (rule is null || rule.DaylightDelta == TimeSpan.Zero)
            return null;

        var startRule = ToPosixTransition(rule.DaylightTransitionStart);
        var endRule = ToPosixTransition(rule.DaylightTransitionEnd);
        if (startRule is null || endRule is null)
            return null;

        var stdOffset = tz.BaseUtcOffset + rule.BaseUtcOffsetDelta;
        var dstOffset = stdOffset + rule.DaylightDelta;
        return Compose(stdOffset, dstOffset, startRule, endRule);
    }

    private static TimeZoneInfo.AdjustmentRule? FindCurrentRule(TimeZoneInfo tz)
    {
        var rules = tz.GetAdjustmentRules();
        if (rules.Length == 0) return null;

        var today = DateTime.Now.Date;
        foreach (var r in rules)
            if (r.DateStart <= today && today <= r.DateEnd)
                return r;

        // No rule covers today (e.g. all historical) — use the most recent one.
        return rules[^1];
    }

    /// <summary>
    /// Finds the zone's actual offset changes during <paramref name="year"/> by
    /// sampling <see cref="TimeZoneInfo.GetUtcOffset(DateTimeOffset)"/> day by day,
    /// then converts the two DST transitions to floating POSIX rules. Returns null
    /// unless the year contains exactly one DST period.
    /// </summary>
    private static string? FromProbedTransitions(TimeZoneInfo tz, int year)
    {
        var cursor = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var yearEnd = cursor.AddYears(1);
        var transitions = new List<(DateTimeOffset Utc, TimeSpan Before, TimeSpan After)>();

        var prev = cursor;
        var prevOffset = tz.GetUtcOffset(prev);
        for (var t = cursor.AddDays(1); t <= yearEnd && transitions.Count < 3; t = t.AddDays(1))
        {
            var offset = tz.GetUtcOffset(t);
            if (offset != prevOffset)
                transitions.Add((RefineTransition(tz, prev, t, offset), prevOffset, offset));
            prev = t;
            prevOffset = offset;
        }

        if (transitions.Count != 2)
            return null;

        // Which period is DST: between the two transitions (northern hemisphere)
        // or the outer part spanning New Year (southern hemisphere).
        var midpoint = transitions[0].Utc +
                       TimeSpan.FromTicks((transitions[1].Utc - transitions[0].Utc).Ticks / 2);
        var (dstStart, dstEnd) = tz.IsDaylightSavingTime(midpoint)
            ? (transitions[0], transitions[1])
            : (transitions[1], transitions[0]);

        if (dstStart.Before != dstEnd.After || dstStart.After != dstEnd.Before)
            return null;

        // POSIX transition times are wall-clock in the time in effect before the change.
        var startRule = FloatingRuleFor(dstStart.Utc.UtcDateTime + dstStart.Before);
        var endRule = FloatingRuleFor(dstEnd.Utc.UtcDateTime + dstEnd.Before);
        return Compose(dstStart.Before, dstStart.After, startRule, endRule);
    }

    /// <summary>
    /// Narrows an offset change known to lie in (<paramref name="before"/>,
    /// <paramref name="after"/>] down to the second and returns the first instant
    /// at which the new offset applies.
    /// </summary>
    private static DateTimeOffset RefineTransition(
        TimeZoneInfo tz, DateTimeOffset before, DateTimeOffset after, TimeSpan afterOffset)
    {
        while (after - before > TimeSpan.FromSeconds(1))
        {
            var mid = before + TimeSpan.FromTicks((after - before).Ticks / 2);
            if (tz.GetUtcOffset(mid) == afterOffset) after = mid;
            else before = mid;
        }
        return after;
    }

    /// <summary>
    /// Generalizes one concrete transition (e.g. 2026-03-29 03:00, a Sunday) to a
    /// floating POSIX rule (M3.5.0/3). Week 5 is used whenever the weekday
    /// occurrence is the last of its month, matching the "last X-day of the month"
    /// phrasing real DST laws use, so the rule stays valid in later years.
    /// </summary>
    private static string FloatingRuleFor(DateTime local)
    {
        var week = (local.Day - 1) / 7 + 1;
        if (local.Day + 7 > DateTime.DaysInMonth(local.Year, local.Month))
            week = 5;

        var timeOfDay = TimeSpan.FromMinutes(Math.Round(local.TimeOfDay.TotalMinutes));
        return FormatPosixRule(local.Month, week, (int)local.DayOfWeek, timeOfDay);
    }

    private static string Compose(TimeSpan stdOffset, TimeSpan dstOffset, string startRule, string endRule)
        => $"{MakeAbbreviation(stdOffset)}{PosixOffset(stdOffset)}" +
           $"{MakeAbbreviation(dstOffset)}{PosixOffset(dstOffset)},{startRule},{endRule}";

    private static string OffsetOnly(TimeSpan offset) => MakeAbbreviation(offset) + PosixOffset(offset);

    /// <summary>
    /// POSIX offset is the value ADDED to local time to reach UTC, so its sign is
    /// inverted relative to the usual UTC offset: UTC+1 becomes "-1". Emits minutes
    /// and seconds only when non-zero (e.g. "-5:30", "-5:45").
    /// </summary>
    private static string PosixOffset(TimeSpan utcOffset)
    {
        var posix = -utcOffset; // invert sign per POSIX convention
        var sign = posix < TimeSpan.Zero ? "-" : "";
        var abs = posix.Duration();

        var sb = new StringBuilder(sign);
        sb.Append(((int)abs.TotalHours).ToString(CultureInfo.InvariantCulture));
        if (abs.Minutes != 0 || abs.Seconds != 0)
        {
            sb.Append(':').Append(abs.Minutes.ToString("D2", CultureInfo.InvariantCulture));
            if (abs.Seconds != 0)
                sb.Append(':').Append(abs.Seconds.ToString("D2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// .NET doesn't expose real zone abbreviations cross-platform, so we emit the
    /// POSIX angle-bracket numeric form (e.g. "&lt;+01&gt;", "&lt;-0530&gt;"). It's a valid
    /// abbreviation that uniquely encodes the offset and differs between std/dst.
    /// </summary>
    private static string MakeAbbreviation(TimeSpan utcOffset)
    {
        var sign = utcOffset < TimeSpan.Zero ? "-" : "+";
        var abs = utcOffset.Duration();
        var body = abs.Minutes != 0
            ? $"{(int)abs.TotalHours:D2}{abs.Minutes:D2}"
            : $"{(int)abs.TotalHours:D2}";
        return $"<{sign}{body}>";
    }

    /// <summary>
    /// Converts a .NET <see cref="TimeZoneInfo.TransitionTime"/> to the POSIX
    /// <c>Mm.w.d[/time]</c> form. Returns null for fixed-date rules, which cannot
    /// be represented in POSIX form.
    /// </summary>
    private static string? ToPosixTransition(TimeZoneInfo.TransitionTime t)
        => t.IsFixedDateRule
            ? null
            : FormatPosixRule(t.Month, t.Week, (int)t.DayOfWeek, t.TimeOfDay.TimeOfDay);

    /// <summary>
    /// Formats the POSIX <c>Mm.w.d[/time]</c> transition rule. .NET and POSIX agree
    /// on the encoding: month 1-12, week 1-5 (5 = last), day-of-week 0 = Sunday.
    /// </summary>
    private static string FormatPosixRule(int month, int week, int dayOfWeek, TimeSpan timeOfDay)
    {
        var rule = $"M{month}.{week}.{dayOfWeek}";

        // POSIX default transition time is 02:00; only append when it differs.
        if (timeOfDay == TimeSpan.FromHours(2))
            return rule;

        var sb = new StringBuilder(rule).Append('/');
        sb.Append(((int)timeOfDay.TotalHours).ToString(CultureInfo.InvariantCulture));
        if (timeOfDay.Minutes != 0 || timeOfDay.Seconds != 0)
        {
            sb.Append(':').Append(timeOfDay.Minutes.ToString("D2", CultureInfo.InvariantCulture));
            if (timeOfDay.Seconds != 0)
                sb.Append(':').Append(timeOfDay.Seconds.ToString("D2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
