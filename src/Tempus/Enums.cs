namespace Tempus;

/// <summary>Lifecycle status of a time ticker or cron occurrence.</summary>
public enum TickerStatus
{
    /// <summary>Created, not yet queued.</summary>
    Idle = 0,
    /// <summary>Acquired by a node and waiting for execution capacity.</summary>
    Queued = 1,
    /// <summary>Executing.</summary>
    InProgress = 2,
    /// <summary>Finished successfully.</summary>
    Done = 3,
    /// <summary>Finished successfully through the overdue path (started late). Not a failure.</summary>
    DueDone = 4,
    /// <summary>Terminal failure (retry budget exhausted).</summary>
    Failed = 5,
    /// <summary>Stopped by cancellation (pending cancel, cooperative cancel or timeout).</summary>
    Cancelled = 6,
    /// <summary>The handler did not run because a recorded policy or condition applied.</summary>
    Skipped = 7,
}

public static class TickerStatusExtensions
{
    public static bool IsTerminal(this TickerStatus status) => status is TickerStatus.Done or TickerStatus.DueDone or TickerStatus.Failed or TickerStatus.Cancelled or TickerStatus.Skipped;
    public static bool IsSuccess(this TickerStatus status) => status is TickerStatus.Done or TickerStatus.DueDone;
    public static bool IsPending(this TickerStatus status) => status is TickerStatus.Idle or TickerStatus.Queued;
    public static bool IsActive(this TickerStatus status) => status is TickerStatus.Idle or TickerStatus.Queued or TickerStatus.InProgress;
}

public enum TickerType
{
    TimeTicker = 0,
    CronTickerOccurrence = 1,
}

/// <summary>Host-local execution ordering. Queues are served in a weighted 4:2:1 cycle (High:Normal:Low).</summary>
public enum TickerTaskPriority
{
    Normal = 0,
    High = 1,
    Low = 2,
    /// <summary>Runs on a dedicated thread; queued with Normal priority.</summary>
    LongRunning = 3,
}

/// <summary>Condition a chained child evaluates against its direct parent's status.</summary>
public enum RunCondition
{
    OnSuccess = 0,
    OnFailure = 1,
    OnCancelled = 2,
    OnFailureOrCancelled = 3,
    /// <summary>Parent reaches a terminal status other than Skipped.</summary>
    OnAnyCompletedStatus = 4,
    /// <summary>Parent reaches any terminal status, including Skipped.</summary>
    OnAnyTerminalStatus = 5,
    /// <summary>The child may start while the parent is running.</summary>
    InProgress = 6,
}

/// <summary>Join continuation kinds for durable groups.</summary>
public enum GroupJoinKind
{
    /// <summary>Not a join continuation (regular branch or step).</summary>
    None = 0,
    /// <summary>Runs when every branch succeeds.</summary>
    AfterAll = 1,
    /// <summary>Runs when at least one branch fails.</summary>
    AfterAnyFailed = 2,
    /// <summary>Runs when a branch fails or is cancelled.</summary>
    AfterAnyFailedOrCancelled = 3,
    /// <summary>Runs when every branch settles, including skipped branches.</summary>
    AfterAllCompleted = 4,
}

public enum ChainDeleteBehavior
{
    /// <summary>Deleting the root deletes the whole chain.</summary>
    Cascade = 0,
    /// <summary>Deletion is blocked while unfinished descendants exist.</summary>
    Restrict = 1,
}

public enum StaleAction
{
    /// <summary>The job can run again, up to MaxStaleRestarts.</summary>
    Restart = 0,
    /// <summary>Record a cancelled stale execution instead of restarting.</summary>
    Cancel = 1,
}

/// <summary>What to do when a one-time job's requested time is blocked by its schedule policy.</summary>
public enum BlockedOccurrenceBehavior
{
    /// <summary>Nothing is saved; the add call fails.</summary>
    Reject = 0,
    /// <summary>Stored as Skipped; no function runs.</summary>
    Skip = 1,
    /// <summary>Moved to the next allowed instant.</summary>
    MoveForward = 2,
}

public enum CronCatchUpMode
{
    /// <summary>Missed slots are not created.</summary>
    Skip = 0,
    /// <summary>Create at most one occurrence: the latest missed slot still inside the SkipAfter window.</summary>
    RunLatest = 1,
}

public enum TempusStartMode
{
    /// <summary>Start processing when the host starts.</summary>
    Immediate = 0,
    /// <summary>Register the scheduler but do not process work until ITempusHostScheduler.StartAsync is called.</summary>
    Manual = 1,
}

/// <summary>Where a time ticker came from.</summary>
public enum TickerOrigin
{
    Manual = 0,
    Seed = 1,
    CronChain = 2,
    Retry = 3,
}
