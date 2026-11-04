namespace Tempus.Entities;

/// <summary>A named, reusable calendar that decides whether work may start at a given instant.</summary>
public class SchedulePolicyEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Namespace { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>Bit flags: 1 &lt;&lt; (int)DayOfWeek. 127 = all days.</summary>
    public int AllowedWeekdays { get; set; } = 127;

    public TimeOnly? WindowStart { get; set; }
    public TimeOnly? WindowEnd { get; set; }

    public List<DateOnly> ExcludedDates { get; set; } = [];

    /// <summary>Month*100 + day, e.g. 1225 for 25 December.</summary>
    public List<int> ExcludedAnnualDates { get; set; } = [];

    public List<string> DateProviders { get; set; } = [];
    public List<string> Requires { get; set; } = [];
    public List<string> ExceptWhen { get; set; } = [];

    public int Revision { get; set; } = 1;
    public bool IsSeeded { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
