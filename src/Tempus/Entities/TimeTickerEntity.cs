namespace Tempus.Entities;

/// <summary>One scheduled job with its own input and execution state. Chains are trees of time tickers linked by <see cref="ParentId"/>.</summary>
public class TimeTickerEntity : TickerExecutionEntity
{
    public override TickerType TickerType => TickerType.TimeTicker;

    public Guid? ParentId { get; set; }

    /// <summary>Root of the chain this job belongs to (null for a standalone job or the root itself).</summary>
    public Guid? ChainRootId { get; set; }

    public RunCondition RunCondition { get; set; } = RunCondition.OnSuccess;

    /// <summary>True for the structural marker node of a durable group (ThenMany). Invokes no user function.</summary>
    public bool IsGroup { get; set; }

    /// <summary>For join continuations attached to a group marker.</summary>
    public GroupJoinKind JoinKind { get; set; } = GroupJoinKind.None;

    public ChainDeleteBehavior DeleteBehavior { get; set; } = ChainDeleteBehavior.Cascade;

    public string? SchedulePolicyName { get; set; }
    public int? SchedulePolicyRevision { get; set; }
    public BlockedOccurrenceBehavior WhenBlocked { get; set; } = BlockedOccurrenceBehavior.Reject;

    /// <summary>Key used by SeedTimeTickers to create a ticker exactly once.</summary>
    public string? SeedKey { get; set; }

    public TickerOrigin Origin { get; set; } = TickerOrigin.Manual;

    /// <summary>Cron occurrence that materialized this chain root, if any.</summary>
    public Guid? CronOccurrenceId { get; set; }

    /// <summary>Depth in the chain (root = 0).</summary>
    public int Depth { get; set; }

    /// <summary>Child jobs. Used when creating graphs through the entity API; not loaded automatically by providers.</summary>
    public List<TimeTickerEntity> Children { get; set; } = [];

    public bool IsRoot => ParentId is null;
}
