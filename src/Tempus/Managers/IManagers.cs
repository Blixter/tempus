using System.Linq.Expressions;
using Tempus.Entities;

namespace Tempus.Managers;

/// <summary>Creates and controls one-time jobs and chains. Inject <c>ITimeTickerManager&lt;TimeTickerEntity&gt;</c>.</summary>
public interface ITimeTickerManager<TEntity> where TEntity : TimeTickerEntity
{
    Task<TickerResult<TEntity>> AddAsync(TEntity entity, CancellationToken ct = default);
    Task<TickerResult<TEntity>> AddAsync<TFunction>(DateTime executionTimeUtc, CancellationToken ct = default);
    Task<TickerResult<TEntity>> AddAsync<TFunction>(TimeSchedule schedule, CancellationToken ct = default);
    Task<TickerResult<TEntity>> AddAsync<TFunction, TRequest>(DateTime executionTimeUtc, TRequest request, CancellationToken ct = default);
    Task<TickerResult<TEntity>> AddAsync<TFunction, TRequest>(TimeSchedule schedule, TRequest request, CancellationToken ct = default);
    Task<TickerResult<TEntity>> AddChainAsync(Action<TimeChainBuilder> build, CancellationToken ct = default);
    Task<TickerResult<IReadOnlyList<TEntity>>> AddBatchAsync(IReadOnlyList<TEntity> entities, CancellationToken ct = default);

    /// <summary>Stage a job in the application's unit of work (EF Core). Persisted when the application saves.</summary>
    Task<TEntity> StageAsync(TEntity entity, CancellationToken ct = default);

    Task<TickerResult<TEntity>> UpdateAsync(TEntity entity, CancellationToken ct = default);
    Task<TickerResult<IReadOnlyList<TEntity>>> UpdateBatchAsync(IReadOnlyList<TEntity> entities, CancellationToken ct = default);
    Task<TickerResult<TEntity>> ReplaceChainAsync(Guid rootId, TEntity newRoot, CancellationToken ct = default);
    Task<TickerResult<TEntity>> RescheduleAsync(Guid id, DateTime executionTimeUtc, CancellationToken ct = default);
    Task<TickerResult<TEntity>> SetDeleteBehaviorAsync(Guid rootId, ChainDeleteBehavior behavior, CancellationToken ct = default);

    Task<TickerResult<TEntity>> RunNowAsync(Guid id, CancellationToken ct = default);
    Task<TickerResult<TEntity>> RetryFromAsync(Guid stepId, CancellationToken ct = default);

    Task<TickerResult<TEntity>> CancelAsync(Guid id, CancellationToken ct = default);
    Task<TickerResult<int>> CancelPendingAsync<TFunction>(CancellationToken ct = default);
    Task<TickerResult<int>> CancelPendingAsync(string function, CancellationToken ct = default);
    Task<TickerResult<int>> RequestCancellationAsync(Guid id, CancellationToken ct = default);

    Task<ExecutionOutcome?> GetOutcomeAsync(Guid id, CancellationToken ct = default);
    Task<ExecutionOutcome?> WaitForCompletionAsync(Guid id, TimeSpan timeout, CancellationToken ct = default);

    Task<TEntity?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<TEntity>> GetChainAsync(Guid rootId, CancellationToken ct = default);
    Task<IReadOnlyList<TEntity>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TEntity>> ListAsync(Expression<Func<TEntity, bool>> filter, CancellationToken ct = default);
    Task<IReadOnlyList<TEntity>> ListAsync<TFunction>(CancellationToken ct = default);

    Task<TickerResult<int>> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<TickerResult<int>> DeleteAsync(Expression<Func<TEntity, bool>> filter, CancellationToken ct = default);
    Task<TickerResult<int>> DeleteBatchAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}

/// <summary>Creates and controls recurring definitions. Inject <c>ICronTickerManager&lt;CronTickerEntity&gt;</c>.</summary>
public interface ICronTickerManager<TEntity> where TEntity : CronTickerEntity
{
    Task<TickerResult<TEntity>> AddAsync(TEntity entity, CancellationToken ct = default);
    Task<TickerResult<TEntity>> AddAsync<TFunction>(CronSchedule schedule, CancellationToken ct = default);
    Task<TickerResult<TEntity>> AddAsync<TFunction, TRequest>(CronSchedule schedule, TRequest request, CancellationToken ct = default);
    Task<TickerResult<TEntity>> AddChainAsync(Action<CronChainBuilder> build, CancellationToken ct = default);
    Task<TickerResult<IReadOnlyList<TEntity>>> AddBatchAsync(IReadOnlyList<TEntity> entities, CancellationToken ct = default);
    Task<TEntity> StageAsync(TEntity entity, CancellationToken ct = default);

    Task<TickerResult<TEntity>> UpdateAsync(TEntity entity, CancellationToken ct = default);
    Task<TickerResult<IReadOnlyList<TEntity>>> UpdateBatchAsync(IReadOnlyList<TEntity> entities, CancellationToken ct = default);
    Task<TickerResult<TEntity>> SetEnabledAsync(Guid cronId, bool enabled, CancellationToken ct = default);

    /// <summary>Create a new manual occurrence now (respects the overlap policy).</summary>
    Task<TickerResult<CronTickerOccurrenceEntity>> RunNowAsync(Guid cronId, CancellationToken ct = default);
    /// <summary>Revive an existing occurrence, keeping its id.</summary>
    Task<TickerResult<CronTickerOccurrenceEntity>> RerunOccurrenceAsync(Guid occurrenceId, CancellationToken ct = default);
    Task<TickerResult<int>> RequestCancellationAsync(Guid occurrenceId, CancellationToken ct = default);
    Task<TickerResult<CronTickerOccurrenceEntity>> CancelOccurrenceAsync(Guid occurrenceId, CancellationToken ct = default);

    Task<IReadOnlyList<DateTime>> GetNextOccurrencesAsync(Guid cronId, int count, CancellationToken ct = default);
    Task<ExecutionOutcome?> GetOutcomeAsync(Guid occurrenceId, CancellationToken ct = default);
    Task<ExecutionOutcome?> WaitForCompletionAsync(Guid occurrenceId, TimeSpan timeout, CancellationToken ct = default);

    Task<TEntity?> GetAsync(Guid cronId, CancellationToken ct = default);
    Task<CronTickerOccurrenceEntity?> GetOccurrenceAsync(Guid occurrenceId, CancellationToken ct = default);
    Task<IReadOnlyList<CronTickerOccurrenceEntity>> ListOccurrencesAsync(Guid cronId, int take = 100, CancellationToken ct = default);
    Task<IReadOnlyList<TEntity>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TEntity>> ListAsync(Expression<Func<TEntity, bool>> filter, CancellationToken ct = default);
    Task<IReadOnlyList<TEntity>> ListAsync<TFunction>(CancellationToken ct = default);

    Task<TickerResult<int>> DeleteAsync(Guid cronId, CancellationToken ct = default);
    Task<TickerResult<int>> DeleteAsync(Expression<Func<TEntity, bool>> filter, CancellationToken ct = default);
    Task<TickerResult<int>> DeleteBatchAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<TickerResult<int>> DeleteOccurrenceAsync(Guid occurrenceId, CancellationToken ct = default);
}

/// <summary>Manage schedule policies at runtime.</summary>
public interface ISchedulePolicyManager
{
    Task<IReadOnlyList<SchedulePolicyEntity>> ListAsync(CancellationToken ct = default);
    Task<SchedulePolicyEntity?> GetAsync(string name, CancellationToken ct = default);
    /// <summary>Create or update by name. Updates must carry the revision they read.</summary>
    Task<TickerResult<SchedulePolicyEntity>> UpsertAsync(SchedulePolicyEntity policy, CancellationToken ct = default);
    Task<TickerResult<int>> DeleteAsync(string name, CancellationToken ct = default);
    Task<Policies.PolicyEvaluation> EvaluateAsync(string name, DateTime utcInstant, CancellationToken ct = default);
    /// <summary>Preview the next cron candidates against the policy (allowed or blocked with reason).</summary>
    Task<IReadOnlyList<(DateTime Utc, bool Allowed, string? Reason)>> PreviewAsync(string name, string cronExpression, string? timeZoneId, int count = 20, CancellationToken ct = default);
}

/// <summary>Hook used by EF Core to stage entities inside the application's unit of work.</summary>
public interface ITickerStagingProvider
{
    Task StageTimeTickersAsync(IReadOnlyList<TimeTickerEntity> entities, CancellationToken ct);
    Task StageCronTickerAsync(CronTickerEntity entity, CancellationToken ct);
}
