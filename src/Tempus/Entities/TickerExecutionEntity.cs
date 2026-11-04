namespace Tempus.Entities;

/// <summary>
/// Shared execution state for anything the scheduler can run (time tickers and cron occurrences).
/// Not mapped as an entity itself; concrete entities inherit the columns.
/// </summary>
public abstract class TickerExecutionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Application namespace (runtime partition).</summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>Registered function name.</summary>
    public string Function { get; set; } = string.Empty;

    /// <summary>Serialized JSON request payload (see <see cref="Tempus.TickerHelper.CreateTickerRequest{T}"/>).</summary>
    public string? Request { get; set; }

    public string? Description { get; set; }

    /// <summary>UTC time the execution is scheduled for. Null means "not released yet" (chained child).</summary>
    public DateTime? ExecutionTime { get; set; }

    /// <summary>The originally requested time before any schedule-policy adjustment.</summary>
    public DateTime? RequestedExecutionTime { get; set; }

    /// <summary>Why ExecutionTime differs from RequestedExecutionTime, if it does.</summary>
    public string? AdjustmentReason { get; set; }

    public TickerStatus Status { get; set; } = TickerStatus.Idle;

    /// <summary>Number of automatic retries after the first failed attempt.</summary>
    public int Retries { get; set; }

    /// <summary>Delay in seconds before each retry. Last value repeats when shorter than <see cref="Retries"/>. Default 30s.</summary>
    public int[] RetryIntervals { get; set; } = [];

    public int RetryCount { get; set; }

    /// <summary>Per-attempt timeout. Null inherits scheduler default; zero or negative disables.</summary>
    public int? TimeoutSeconds { get; set; }

    public TickerTaskPriority Priority { get; set; } = TickerTaskPriority.Normal;

    /// <summary>Grace window in seconds: if the job starts later than this after ExecutionTime it is Skipped. Null = run whenever possible.</summary>
    public int? SkipAfterSeconds { get; set; }

    public StaleAction OnStale { get; set; } = StaleAction.Restart;
    public int? MaxStaleRestarts { get; set; }
    public int StaleRestartCount { get; set; }

    // ---- ownership / lease ----
    public string? LockHolder { get; set; }
    public DateTime? LockedAt { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public Guid? AcquisitionToken { get; set; }

    // ---- outcome ----
    public DateTime? StartedAt { get; set; }
    public DateTime? ExecutedAt { get; set; }
    public long? ElapsedMilliseconds { get; set; }
    public string? Exception { get; set; }
    public string? ExceptionType { get; set; }
    public string? SkipReason { get; set; }

    /// <summary>Serialized JSON result published after success. Null means no result was published.</summary>
    public string? Result { get; set; }
    public bool HasResult { get; set; }

    // ---- progress ----
    public int? ProgressPercent { get; set; }
    public string? ProgressMessage { get; set; }
    public DateTime? ProgressUpdatedAt { get; set; }

    /// <summary>Increments on every (re)acquisition. Late writes from an old attempt are rejected.</summary>
    public int Generation { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool CancellationRequested { get; set; }

    public abstract TickerType TickerType { get; }

    public void ClearOutcome()
    {
        StartedAt = null;
        ExecutedAt = null;
        ElapsedMilliseconds = null;
        Exception = null;
        ExceptionType = null;
        SkipReason = null;
        Result = null;
        HasResult = false;
        ProgressPercent = null;
        ProgressMessage = null;
        ProgressUpdatedAt = null;
        RetryCount = 0;
        LockHolder = null;
        LockedAt = null;
        LeaseUntil = null;
        AcquisitionToken = null;
        CancellationRequested = false;
    }
}
