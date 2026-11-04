using System.Linq.Expressions;
using Tempus.Entities;

namespace Tempus.Persistence;

/// <summary>Filter for retention sweeps.</summary>
public sealed record RetentionWindow(TickerStatus Status, DateTime Before);

/// <summary>
/// Storage abstraction. Every call is scoped to one application namespace (the provider is constructed with it).
/// Implementations: in-memory (core), EF Core (Tempus.EntityFrameworkCore).
/// </summary>
public interface ITickerPersistenceProvider
{
    string Namespace { get; }
    bool SupportsLeaseBasedRecovery { get; }
    bool SupportsCronChains => true;

    // ------------------------------------------------------------------ time tickers
    Task<TimeTickerEntity?> GetTimeTickerAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<TimeTickerEntity>> ListTimeTickersAsync(Expression<Func<TimeTickerEntity, bool>>? filter, int? take = null, CancellationToken ct = default);
    /// <summary>Insert a graph (root and all descendants) atomically.</summary>
    Task InsertTimeTickersAsync(IReadOnlyList<TimeTickerEntity> entities, CancellationToken ct = default);
    /// <summary>Full update. Returns false when the row no longer exists.</summary>
    Task<bool> UpdateTimeTickerAsync(TimeTickerEntity entity, CancellationToken ct = default);
    /// <summary>Update several rows (chain operations). Best-effort atomic.</summary>
    Task UpdateTimeTickersAsync(IReadOnlyList<TimeTickerEntity> entities, CancellationToken ct = default);
    Task<int> DeleteTimeTickersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<int> DeleteTimeTickersAsync(Expression<Func<TimeTickerEntity, bool>> filter, CancellationToken ct = default);
    /// <summary>All members of a chain (root + descendants), including the root.</summary>
    Task<IReadOnlyList<TimeTickerEntity>> GetChainAsync(Guid rootId, CancellationToken ct = default);
    Task<IReadOnlyList<TimeTickerEntity>> GetChildrenAsync(Guid parentId, CancellationToken ct = default);
    /// <summary>Atomically acquire due, idle, released time tickers: sets Queued + lock fields + token. Returns the acquired rows.</summary>
    Task<IReadOnlyList<TimeTickerEntity>> AcquireDueTimeTickersAsync(string ownerId, DateTime now, DateTime leaseUntil, int batchSize, CancellationToken ct = default);
    Task<DateTime?> GetNextTimeTickerExecutionTimeAsync(DateTime after, CancellationToken ct = default);
    Task<IReadOnlyList<TimeTickerEntity>> FindExpiredTimeTickerLeasesAsync(DateTime now, int batchSize, CancellationToken ct = default);
    Task<IReadOnlyList<TimeTickerEntity>> ListTerminalTimeTickerRootsAsync(RetentionWindow window, int batchSize, CancellationToken ct = default);

    // ------------------------------------------------------------------ cron tickers
    Task<CronTickerEntity?> GetCronTickerAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<CronTickerEntity>> ListCronTickersAsync(Expression<Func<CronTickerEntity, bool>>? filter, CancellationToken ct = default);
    Task InsertCronTickerAsync(CronTickerEntity entity, CancellationToken ct = default);
    Task<bool> UpdateCronTickerAsync(CronTickerEntity entity, CancellationToken ct = default);
    Task<int> DeleteCronTickersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<int> DeleteCronTickersAsync(Expression<Func<CronTickerEntity, bool>> filter, CancellationToken ct = default);

    // ------------------------------------------------------------------ cron occurrences
    Task<CronTickerOccurrenceEntity?> GetOccurrenceAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<CronTickerOccurrenceEntity>> ListOccurrencesAsync(Expression<Func<CronTickerOccurrenceEntity, bool>>? filter, int? take = null, CancellationToken ct = default);
    /// <summary>Insert unless a non-manual occurrence with the same cron id and execution time exists. Returns false when it already existed.</summary>
    Task<bool> TryInsertOccurrenceAsync(CronTickerOccurrenceEntity entity, CancellationToken ct = default);
    Task<bool> UpdateOccurrenceAsync(CronTickerOccurrenceEntity entity, CancellationToken ct = default);
    Task<int> DeleteOccurrencesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<int> DeleteOccurrencesAsync(Expression<Func<CronTickerOccurrenceEntity, bool>> filter, CancellationToken ct = default);
    Task<IReadOnlyList<CronTickerOccurrenceEntity>> AcquireDueOccurrencesAsync(string ownerId, DateTime now, DateTime leaseUntil, int batchSize, CancellationToken ct = default);
    Task<DateTime?> GetNextOccurrenceExecutionTimeAsync(DateTime after, CancellationToken ct = default);
    Task<IReadOnlyList<CronTickerOccurrenceEntity>> FindExpiredOccurrenceLeasesAsync(DateTime now, int batchSize, CancellationToken ct = default);
    Task<IReadOnlyList<CronTickerOccurrenceEntity>> ListTerminalOccurrencesAsync(RetentionWindow window, int batchSize, CancellationToken ct = default);
    /// <summary>Whether any occurrence of the cron is Queued or InProgress (other than <paramref name="excludeId"/>), or any launched chain still has active jobs.</summary>
    Task<bool> HasActiveOccurrenceAsync(Guid cronTickerId, Guid excludeId, CancellationToken ct = default);

    // ------------------------------------------------------------------ shared execution helpers
    /// <summary>Renew the lease if the row is still owned under the same token.</summary>
    Task<bool> RenewLeaseAsync(TickerType type, Guid id, Guid token, DateTime leaseUntil, CancellationToken ct = default);
    /// <summary>Write progress if still owned under the same token.</summary>
    Task<bool> UpdateProgressAsync(TickerType type, Guid id, Guid token, int percent, string? message, DateTime now, CancellationToken ct = default);
    /// <summary>Write a terminal (or retry-rescheduled) state if still owned under the same token. Returns false when the token no longer matches.</summary>
    Task<bool> CompleteExecutionAsync(TickerExecutionEntity entity, Guid token, CancellationToken ct = default);
    Task<bool> MarkCancellationRequestedAsync(TickerType type, Guid id, CancellationToken ct = default);

    // ------------------------------------------------------------------ schedule policies
    Task<SchedulePolicyEntity?> GetSchedulePolicyAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<SchedulePolicyEntity>> ListSchedulePoliciesAsync(CancellationToken ct = default);
    Task UpsertSchedulePolicyAsync(SchedulePolicyEntity policy, CancellationToken ct = default);
    Task<bool> DeleteSchedulePolicyAsync(string name, CancellationToken ct = default);
    Task<int> CountSchedulePolicyReferencesAsync(string name, CancellationToken ct = default);

    // ------------------------------------------------------------------ nodes & control commands
    Task UpsertNodeAsync(TempusNodeEntity node, CancellationToken ct = default);
    Task<IReadOnlyList<TempusNodeEntity>> ListNodesAsync(CancellationToken ct = default);
    Task RemoveNodeAsync(string ownerId, CancellationToken ct = default);
    Task InsertControlCommandAsync(ControlCommandEntity command, CancellationToken ct = default);
    /// <summary>Returns and removes pending commands for the owner. Expired commands are discarded.</summary>
    Task<IReadOnlyList<ControlCommandEntity>> DequeueControlCommandsAsync(string ownerId, DateTime now, CancellationToken ct = default);

    // ------------------------------------------------------------------ execution logs
    Task UpsertExecutionLogAsync(ExecutionLogEntity log, CancellationToken ct = default);
    Task<ExecutionLogEntity?> GetExecutionLogAsync(Guid tickerId, CancellationToken ct = default);
    Task<int> DeleteExecutionLogsAsync(DateTime olderThan, int keepNewest, CancellationToken ct = default);

    // ------------------------------------------------------------------ metadata
    Task<string?> GetMetadataAsync(string key, CancellationToken ct = default);
    Task SetMetadataAsync(string key, string? value, CancellationToken ct = default);

    // ------------------------------------------------------------------ stats (dashboard)
    Task<IReadOnlyDictionary<TickerStatus, int>> CountTimeTickersByStatusAsync(CancellationToken ct = default);
    Task<IReadOnlyDictionary<TickerStatus, int>> CountOccurrencesByStatusAsync(CancellationToken ct = default);
}
