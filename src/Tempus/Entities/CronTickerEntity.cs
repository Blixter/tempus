namespace Tempus.Entities;

/// <summary>A recurring definition with a cron expression.</summary>
public class CronTickerEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Namespace { get; set; } = string.Empty;
    public string Function { get; set; } = string.Empty;

    /// <summary>Five-part (minute granularity) or six-part (seconds first) cron expression.</summary>
    public string Expression { get; set; } = string.Empty;

    public string? Description { get; set; }
    public string? Request { get; set; }
    public int Retries { get; set; }
    public int[] RetryIntervals { get; set; } = [];
    public int? TimeoutSeconds { get; set; }
    public TickerTaskPriority Priority { get; set; } = TickerTaskPriority.Normal;
    public bool Enabled { get; set; } = true;

    /// <summary>Overlap policy: when true a new occurrence is skipped while a previous run is unfinished.</summary>
    public bool SkipIfAlreadyRunning { get; set; }

    /// <summary>IANA or Windows time zone id used to interpret the expression. Null = scheduler time zone.</summary>
    public string? TimeZoneId { get; set; }

    public string? SchedulePolicyName { get; set; }
    public int? SkipAfterSeconds { get; set; }
    public CronCatchUpMode CatchUpMode { get; set; } = CronCatchUpMode.Skip;

    public StaleAction OnStale { get; set; } = StaleAction.Restart;
    public int? MaxStaleRestarts { get; set; }

    /// <summary>Serialized <see cref="CronChainTemplate"/>; when set each occurrence launches a fresh time-ticker chain.</summary>
    public string? ChainTemplateJson { get; set; }

    public ChainDeleteBehavior ChainDeleteBehavior { get; set; } = ChainDeleteBehavior.Restrict;

    /// <summary>Key used by SeedCronTickers; crons with a seed key are code-owned.</summary>
    public string? SeedKey { get; set; }

    /// <summary>Set when a seeded cron was removed from the seed list.</summary>
    public bool Retired { get; set; }

    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public TickerStatus? LastStatus { get; set; }

    /// <summary>Increments when scheduling-relevant fields change. Pending occurrences created under an older definition are skipped.</summary>
    public int DefinitionVersion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Convenience accessor for the chain template.</summary>
    public CronChainTemplate? Chain
    {
        get => ChainTemplateJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<CronChainTemplate>(ChainTemplateJson, TickerHelper.JsonOptions);
        set => ChainTemplateJson = value is null ? null : System.Text.Json.JsonSerializer.Serialize(value, TickerHelper.JsonOptions);
    }
}
