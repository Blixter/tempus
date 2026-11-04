namespace Tempus.Entities;

/// <summary>One scheduled or manually triggered run of a cron definition.</summary>
public class CronTickerOccurrenceEntity : TickerExecutionEntity
{
    public override TickerType TickerType => TickerType.CronTickerOccurrence;

    public Guid CronTickerId { get; set; }

    /// <summary>Definition version of the cron when this occurrence was planned.</summary>
    public int DefinitionVersion { get; set; }

    public bool IsManual { get; set; }

    /// <summary>Root of the time-ticker chain launched by this occurrence (recurring chains).</summary>
    public Guid? ChainRootId { get; set; }
}
