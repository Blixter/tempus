namespace Tempus.Options;

/// <summary>Scheduler settings (<c>options.ConfigureScheduler(s => ...)</c>).</summary>
public sealed class SchedulerOptions
{
    /// <summary>Readable worker label. The execution owner id is derived from it plus process id and a per-process value.</summary>
    public string NodeIdentifier { get; set; } = Environment.MachineName;
    public string? NodeDisplayName { get; set; }
    public string? AdvertisedDashboardUrl { get; set; }

    /// <summary>Time zone used for cron expressions that set no zone of their own. Set it explicitly on every replica.</summary>
    public TimeZoneInfo SchedulerTimeZone { get; set; } = TimeZoneInfo.Local;

    /// <summary>Maximum executing function attempts per host, including chain steps.</summary>
    public int MaxConcurrency { get; set; } = Environment.ProcessorCount;

    /// <summary>Maximum time-ticker roots loaded per acquisition.</summary>
    public int AcquisitionBatchSize { get; set; } = 100;

    /// <summary>Maximum admitted attempts waiting for execution capacity.</summary>
    public int MaxQueuedExecutions { get; set; } = 1024;

    /// <summary>Minimum calculated scheduler wait.</summary>
    public TimeSpan MinPollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Delay after an empty or failed overdue-work scan.</summary>
    public TimeSpan FallbackIntervalChecker { get; set; } = TimeSpan.FromSeconds(30);
    public int FallbackBatchSize { get; set; } = 100;

    /// <summary>No default attempt timeout unless set.</summary>
    public TimeSpan? DefaultExecutionTimeout { get; set; }

    /// <summary>Delay before a still-running, cancellation-insensitive delegate is reported as timeout-pending.</summary>
    public TimeSpan TimeoutGracePeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Bounded wait for in-flight work on shutdown.</summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Executions that start later than this after their scheduled time are flagged as due (DueDone on success).</summary>
    public TimeSpan DueThreshold { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan LeaseRenewalInterval { get; set; } = TimeSpan.FromSeconds(15);
    public bool StaleJobRecoveryEnabled { get; set; } = true;
    /// <summary>After this many stale restarts a ticker becomes Cancelled.</summary>
    public int MaxStaleRestarts { get; set; } = 3;
    public TimeSpan StaleWatchdogInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan NodeHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Nodes without a heartbeat for this long are considered offline (and removed by other nodes).</summary>
    public TimeSpan NodeOfflineAfter { get; set; } = TimeSpan.FromMinutes(3);
    public TimeSpan ControlCommandPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How far ahead occurrences are materialized for enabled crons.</summary>
    public TimeSpan CronPlanningHorizon { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan CronPlanningInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound of upcoming occurrences materialized per cron inside the horizon.</summary>
    public int MaxPlannedOccurrencesPerCron { get; set; } = 3;

    public bool DetailedLogging { get; set; }

    internal void Validate()
    {
        if (MaxConcurrency < 1) throw new TempusException("MaxConcurrency must be at least 1.");
        if (LeaseDuration < LeaseRenewalInterval * 3) throw new TempusException("LeaseDuration should be at least 3x LeaseRenewalInterval.");
        if (MinPollingInterval <= TimeSpan.Zero) throw new TempusException("MinPollingInterval must be positive.");
    }
}

/// <summary>Retention of terminal jobs (<c>options.ConfigureJobRetention(r => ...)</c>). Disabled until at least one window is set.</summary>
public sealed class RetentionOptions
{
    public TimeSpan? DeleteSucceededAfter { get; set; }
    public TimeSpan? DeleteFailedAfter { get; set; }
    public TimeSpan? DeleteCancelledAfter { get; set; }
    public TimeSpan? DeleteSkippedAfter { get; set; }
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);
    public int BatchSize { get; set; } = 500;
    public int MaxBatchesPerSweep { get; set; } = 10;
    public int MaxNodesPerChain { get; set; } = 1000;

    public bool Enabled => DeleteSucceededAfter is not null || DeleteFailedAfter is not null || DeleteCancelledAfter is not null || DeleteSkippedAfter is not null;
}

/// <summary>Execution-log capture (<c>options.ConfigureExecutionLogs(l => ...)</c>).</summary>
public sealed class ExecutionLogOptions
{
    public bool Enabled { get; set; } = true;
    public bool PersistToDatabase { get; set; }
    public Microsoft.Extensions.Logging.LogLevel MinimumLevel { get; set; } = Microsoft.Extensions.Logging.LogLevel.Information;
    public List<string> ExcludedCategoryPrefixes { get; set; } = ["Microsoft.EntityFrameworkCore.Database"];
    public bool IncludeLifecycleEvents { get; set; } = true;
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);
    public int MaxStoredExecutions { get; set; } = 5000;
    public int MaxLinesPerExecution { get; set; } = 200;
    public int MaxBytesPerExecution { get; set; } = 32 * 1024;
    /// <summary>Optional message filter applied before storage.</summary>
    public Func<string, string>? Redact { get; set; }
}
