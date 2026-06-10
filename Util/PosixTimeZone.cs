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
    /// <summary>
    /// Returns a POSIX TZ string for <see cref="TimeZoneInfo.Local"/>, or for the
    /// supplied zone. Falls back to a plain UTC-offset string (no DST) when the
    /// zone's daylight rules can't be expressed in POSIX form.
    /// </summary>
    public static string GetLocalPosixTz() => ToPosixTz(TimeZoneInfo.Local);

    public static string ToPosixTz(TimeZoneInfo tz)
    {
        var stdOffset = tz.BaseUtcOffset;
        var stdAbbr = MakeAbbreviation(stdOffset);
        var result = stdAbbr + PosixOffset(stdOffset);

        if (!tz.SupportsDaylightSavingTime)
            return result;

        // Pick the adjustment rule that applies around now; time zones can have
        // several historical rules, and only the current one matters for the clock.
        var rule = FindCurrentRule(tz);
        if (rule is null || rule.DaylightDelta == TimeSpan.Zero)
            return result;

        var startRule = ToPosixTransition(rule.DaylightTransitionStart);
        var endRule = ToPosixTransition(rule.DaylightTransitionEnd);
        if (startRule is null || endRule is null)
            return result; // Fixed-date / unsupported rule — offset-only is safer than wrong DST.

        var dstOffset = stdOffset + rule.DaylightDelta;
        var dstAbbr = MakeAbbreviation(dstOffset);
        return $"{stdAbbr}{PosixOffset(stdOffset)}{dstAbbr}{PosixOffset(dstOffset)},{startRule},{endRule}";
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
    /// <c>Mm.w.d[/time]</c> form. Returns null for fixed-date (Julian) rules, which
    /// real-world zones rarely use and which we'd rather skip than emit incorrectly.
    /// </summary>
    private static string? ToPosixTransition(TimeZoneInfo.TransitionTime t)
    {
        if (t.IsFixedDateRule)
            return null;

        // .NET and POSIX agree on the encoding: month 1-12, week 1-5 (5 = last),
        // day-of-week 0 = Sunday.
        var month = t.Month;
        var week = t.Week;
        var day = (int)t.DayOfWeek;

        var rule = $"M{month}.{week}.{day}";

        // POSIX default transition time is 02:00; only append when it differs.
        var tod = t.TimeOfDay.TimeOfDay;
        if (tod != TimeSpan.FromHours(2))
        {
            var sb = new StringBuilder("/");
            sb.Append(((int)tod.TotalHours).ToString(CultureInfo.InvariantCulture));
            if (tod.Minutes != 0 || tod.Seconds != 0)
            {
                sb.Append(':').Append(tod.Minutes.ToString("D2", CultureInfo.InvariantCulture));
                if (tod.Seconds != 0)
                    sb.Append(':').Append(tod.Seconds.ToString("D2", CultureInfo.InvariantCulture));
            }
            rule += sb.ToString();
        }

        return rule;
    }
}
