using Tempus.Entities;

namespace Tempus.Policies;

/// <summary>Fluent builder for a schedule policy (business calendar).</summary>
public sealed class SchedulePolicy
{
    private readonly SchedulePolicyEntity _entity = new();

    private SchedulePolicy(string name) => _entity.Name = name.Trim();

    public static SchedulePolicy Create(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Policy name is required.", nameof(name));
        return new SchedulePolicy(name);
    }

    public string Name => _entity.Name;

    public SchedulePolicy WithDescription(string description) { _entity.Description = description; return this; }
    public SchedulePolicy InTimeZone(string timeZoneId) { _entity.TimeZoneId = timeZoneId; return this; }
    public SchedulePolicy InTimeZone(TimeZoneInfo timeZone) { _entity.TimeZoneId = timeZone.Id; return this; }

    public SchedulePolicy AllowWeekdays(params DayOfWeek[] days)
    {
        if (days.Length == 0) throw new ArgumentException("At least one weekday must be allowed.", nameof(days));
        _entity.AllowedWeekdays = days.Aggregate(0, (acc, d) => acc | (1 << (int)d));
        return this;
    }

    /// <summary>Start inclusive, end exclusive. An end earlier than the start crosses midnight.</summary>
    public SchedulePolicy AllowBetween(TimeOnly start, TimeOnly end) { _entity.WindowStart = start; _entity.WindowEnd = end; return this; }
    public SchedulePolicy ExcludeDate(DateOnly date) { _entity.ExcludedDates.Add(date); return this; }
    public SchedulePolicy ExcludeAnnualDate(int month, int day)
    {
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(2024, month)) throw new ArgumentException("Invalid annual date.");
        _entity.ExcludedAnnualDates.Add(month * 100 + day);
        return this;
    }
    public SchedulePolicy ExcludeDatesFrom(string providerName) { _entity.DateProviders.Add(providerName); return this; }
    public SchedulePolicy Require(string policyName) { _entity.Requires.Add(policyName); return this; }
    public SchedulePolicy ExceptWhen(string policyName) { _entity.ExceptWhen.Add(policyName); return this; }

    public SchedulePolicyEntity Build()
    {
        return new SchedulePolicyEntity
        {
            Name = _entity.Name,
            Description = _entity.Description,
            TimeZoneId = _entity.TimeZoneId,
            AllowedWeekdays = _entity.AllowedWeekdays,
            WindowStart = _entity.WindowStart,
            WindowEnd = _entity.WindowEnd,
            ExcludedDates = [.. _entity.ExcludedDates],
            ExcludedAnnualDates = [.. _entity.ExcludedAnnualDates],
            DateProviders = [.. _entity.DateProviders],
            Requires = [.. _entity.Requires],
            ExceptWhen = [.. _entity.ExceptWhen],
        };
    }
}

/// <summary>Late-start rule for a persisted occurrence that reaches execution late.</summary>
public sealed class MissedExecutionPolicy
{
    private MissedExecutionPolicy(TimeSpan? skipAfter) => SkipAfterWindow = skipAfter;

    /// <summary>The grace window, or null for RunOnce.</summary>
    public TimeSpan? SkipAfterWindow { get; }

    /// <summary>Run once when capacity returns, however late (default).</summary>
    public static MissedExecutionPolicy RunOnce() => new(null);

    /// <summary>Skip the execution if it starts more than <paramref name="window"/> after its scheduled time.</summary>
    public static MissedExecutionPolicy SkipAfter(TimeSpan window)
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window), "The grace window must be positive.");
        return new(window);
    }

    internal int? SkipAfterSeconds => SkipAfterWindow is null ? null : (int)Math.Ceiling(SkipAfterWindow.Value.TotalSeconds);
}
