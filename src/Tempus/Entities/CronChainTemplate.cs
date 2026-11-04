namespace Tempus.Entities;

/// <summary>Template copied into a fresh time-ticker chain for every cron occurrence.</summary>
public sealed class CronChainTemplate
{
    public List<CronChainNode> Children { get; set; } = [];
}

public sealed class CronChainNode
{
    public string Function { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Request { get; set; }
    public RunCondition RunCondition { get; set; } = RunCondition.OnSuccess;
    public int Retries { get; set; }
    public int[] RetryIntervals { get; set; } = [];
    public int? TimeoutSeconds { get; set; }
    public TickerTaskPriority Priority { get; set; } = TickerTaskPriority.Normal;
    public StaleAction OnStale { get; set; } = StaleAction.Restart;
    public int? MaxStaleRestarts { get; set; }
    public bool IsGroup { get; set; }
    public GroupJoinKind JoinKind { get; set; } = GroupJoinKind.None;
    public List<CronChainNode> Children { get; set; } = [];
}
