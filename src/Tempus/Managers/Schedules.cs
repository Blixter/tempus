using Tempus.Entities;
using Tempus.Policies;

namespace Tempus.Managers;

/// <summary>Builder for a one-time schedule (<c>TimeSchedule.At(...)</c>).</summary>
public sealed class TimeSchedule
{
    internal DateTime? ExecutionTimeUtc { get; private set; }
    internal string? PolicyName { get; private set; }
    internal BlockedOccurrenceBehavior Blocked { get; private set; } = BlockedOccurrenceBehavior.Reject;
    internal int? SkipAfterSeconds { get; private set; }
    internal string? Description { get; private set; }
    internal int Retries { get; private set; }
    internal int[] RetryIntervals { get; private set; } = [];
    internal int? TimeoutSeconds { get; private set; }
    internal TickerTaskPriority? Priority { get; private set; }

    private TimeSchedule() { }

    public static TimeSchedule At(DateTime executionTimeUtc) => new() { ExecutionTimeUtc = DateTime.SpecifyKind(executionTimeUtc.Kind == DateTimeKind.Local ? executionTimeUtc.ToUniversalTime() : executionTimeUtc, DateTimeKind.Utc) };
    public static TimeSchedule At(DateTimeOffset executionTime) => new() { ExecutionTimeUtc = executionTime.UtcDateTime };
    public static TimeSchedule Now() => new();
    public static TimeSchedule After(TimeSpan delay) => new() { ExecutionTimeUtc = DateTime.UtcNow.Add(delay) };

    public TimeSchedule UsingPolicy(string policyName) { PolicyName = policyName; return this; }
    public TimeSchedule WhenBlocked(BlockedOccurrenceBehavior behavior) { Blocked = behavior; return this; }
    public TimeSchedule WhenMissed(MissedExecutionPolicy policy) { SkipAfterSeconds = policy.SkipAfterSeconds; return this; }
    public TimeSchedule WithDescription(string description) { Description = description; return this; }
    public TimeSchedule WithRetries(int retries, int[]? intervals = null) { Retries = retries; RetryIntervals = intervals ?? []; return this; }
    public TimeSchedule WithTimeout(TimeSpan timeout) { TimeoutSeconds = (int)timeout.TotalSeconds; return this; }
    public TimeSchedule WithPriority(TickerTaskPriority priority) { Priority = priority; return this; }

    internal TimeTickerEntity ToEntity(string function, string? requestJson)
    {
        return new TimeTickerEntity
        {
            Function = function,
            Request = requestJson,
            ExecutionTime = ExecutionTimeUtc,
            Description = Description,
            SchedulePolicyName = PolicyName,
            WhenBlocked = Blocked,
            SkipAfterSeconds = SkipAfterSeconds,
            Retries = Retries,
            RetryIntervals = RetryIntervals,
            TimeoutSeconds = TimeoutSeconds,
            Priority = Priority ?? TickerTaskPriority.Normal,
        };
    }
}

/// <summary>Builder for a recurring schedule (<c>CronSchedule.From("0 9 * * 1-5")</c>).</summary>
public sealed class CronSchedule
{
    internal string Expression { get; private set; } = string.Empty;
    internal string? TimeZoneId { get; private set; }
    internal string? PolicyName { get; private set; }
    internal int? SkipAfterSeconds { get; private set; }
    internal CronCatchUpMode CatchUpMode { get; private set; } = CronCatchUpMode.Skip;
    internal bool SkipIfRunning { get; private set; }
    internal bool IsEnabled { get; private set; } = true;
    internal string? Description { get; private set; }
    internal int Retries { get; private set; }
    internal int[] RetryIntervals { get; private set; } = [];
    internal int? TimeoutSeconds { get; private set; }
    internal TickerTaskPriority? Priority { get; private set; }
    internal string? RequestJson { get; private set; }

    private CronSchedule() { }

    public static CronSchedule From(string expression)
    {
        Scheduling.CronParser.Parse(expression); // validate early
        return new CronSchedule { Expression = expression };
    }

    public CronSchedule InTimeZone(string timeZoneId) { TimeZoneId = timeZoneId; return this; }
    public CronSchedule InTimeZone(TimeZoneInfo timeZone) { TimeZoneId = timeZone.Id; return this; }
    public CronSchedule UsingPolicy(string policyName) { PolicyName = policyName; return this; }
    public CronSchedule WhenMissed(MissedExecutionPolicy policy) { SkipAfterSeconds = policy.SkipAfterSeconds; return this; }
    public CronSchedule CatchUp(CronCatchUpMode mode) { CatchUpMode = mode; return this; }
    public CronSchedule SkipIfAlreadyRunning(bool skip = true) { SkipIfRunning = skip; return this; }
    public CronSchedule Enabled(bool enabled) { IsEnabled = enabled; return this; }
    public CronSchedule WithDescription(string description) { Description = description; return this; }
    public CronSchedule WithRetries(int retries, int[]? intervals = null) { Retries = retries; RetryIntervals = intervals ?? []; return this; }
    public CronSchedule WithTimeout(TimeSpan timeout) { TimeoutSeconds = (int)timeout.TotalSeconds; return this; }
    public CronSchedule WithPriority(TickerTaskPriority priority) { Priority = priority; return this; }
    public CronSchedule WithRequest<T>(T request) { RequestJson = TickerHelper.CreateTickerRequest(request); return this; }

    internal CronTickerEntity ToEntity(string function)
    {
        return new CronTickerEntity
        {
            Function = function,
            Expression = Expression,
            TimeZoneId = TimeZoneId,
            SchedulePolicyName = PolicyName,
            SkipAfterSeconds = SkipAfterSeconds,
            CatchUpMode = CatchUpMode,
            SkipIfAlreadyRunning = SkipIfRunning,
            Enabled = IsEnabled,
            Description = Description,
            Retries = Retries,
            RetryIntervals = RetryIntervals,
            TimeoutSeconds = TimeoutSeconds,
            Priority = Priority ?? TickerTaskPriority.Normal,
            Request = RequestJson,
        };
    }
}
