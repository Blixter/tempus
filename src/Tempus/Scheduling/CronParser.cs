using Cronos;

namespace Tempus.Scheduling;

/// <summary>Cron parsing on top of Cronos: 5-part (minutes) or 6-part (seconds first) expressions, with L/W/# support.</summary>
public static class CronParser
{
    public static CronExpression Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) throw new TempusException("Cron expression is required.");
        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            return parts.Length switch
            {
                5 => CronExpression.Parse(expression, CronFormat.Standard),
                6 => CronExpression.Parse(expression, CronFormat.IncludeSeconds),
                _ => throw new TempusException($"Cron expression '{expression}' must have 5 or 6 parts."),
            };
        }
        catch (CronFormatException ex)
        {
            throw new TempusException($"Invalid cron expression '{expression}': {ex.Message}", ex);
        }
    }

    public static bool TryParse(string expression, out CronExpression? parsed)
    {
        try { parsed = Parse(expression); return true; }
        catch { parsed = null; return false; }
    }

    public static DateTime? GetNextOccurrence(string expression, DateTime fromUtc, TimeZoneInfo timeZone, bool inclusive = false) =>
        Parse(expression).GetNextOccurrence(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), timeZone, inclusive);

    public static IEnumerable<DateTime> GetOccurrences(string expression, DateTime fromUtc, DateTime toUtc, TimeZoneInfo timeZone, bool fromInclusive = true) =>
        Parse(expression).GetOccurrences(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), DateTime.SpecifyKind(toUtc, DateTimeKind.Utc), timeZone, fromInclusive, toInclusive: false);

    /// <summary>Human readable description of an expression (approximate).</summary>
    public static string Describe(string expression)
    {
        if (!TryParse(expression, out _)) return "invalid expression";
        var p = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hasSeconds = p.Length == 6;
        var sec = hasSeconds ? p[0] : "0";
        var min = hasSeconds ? p[1] : p[0];
        var hour = hasSeconds ? p[2] : p[1];
        var dom = hasSeconds ? p[3] : p[2];
        var mon = hasSeconds ? p[4] : p[3];
        var dow = hasSeconds ? p[5] : p[4];

        if (hasSeconds && sec.StartsWith("*/") && min == "*" && hour == "*") return $"every {sec[2..]} seconds";
        if (hasSeconds && sec == "*" && min == "*" && hour == "*") return "every second";
        if (min.StartsWith("*/") && hour == "*") return $"every {min[2..]} minutes" + DaysSuffix(dom, mon, dow);
        if (min == "*" && hour == "*") return "every minute" + DaysSuffix(dom, mon, dow);
        if (hour.StartsWith("*/") && int.TryParse(min, out _)) return $"every {hour[2..]} hours at minute {min}" + DaysSuffix(dom, mon, dow);
        if (int.TryParse(min, out var m) && hour == "*") return $"every hour at minute {m}" + DaysSuffix(dom, mon, dow);
        if (int.TryParse(min, out m) && int.TryParse(hour, out var h)) return $"at {h:00}:{m:00}" + DaysSuffix(dom, mon, dow);
        if (int.TryParse(min, out m) && hour.Contains(','))
            return $"at minute {m} past hours {hour}" + DaysSuffix(dom, mon, dow);
        return $"cron {expression}";
    }

    private static string DaysSuffix(string dom, string mon, string dow)
    {
        var parts = new List<string>();
        if (dow != "*" && dow != "?") parts.Add(dow switch
        {
            "1-5" or "MON-FRI" => "on weekdays",
            "0,6" or "6,0" or "SAT,SUN" or "SUN,SAT" => "on weekends",
            _ => $"on days-of-week {dow}",
        });
        if (dom != "*" && dom != "?") parts.Add($"on day {dom} of the month");
        if (mon != "*") parts.Add($"in month {mon}");
        return parts.Count == 0 ? " every day" : " " + string.Join(" ", parts);
    }
}
