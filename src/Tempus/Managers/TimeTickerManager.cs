using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tempus.Entities;
using Tempus.Functions;
using Tempus.Options;
using Tempus.Persistence;
using Tempus.Policies;
using Tempus.Scheduling;

namespace Tempus.Managers;

public sealed class TimeTickerManager : ITimeTickerManager<TimeTickerEntity>
{
    private readonly ITickerPersistenceProvider _provider;
    private readonly TickerFunctionRegistry _registry;
    private readonly TempusOptions _options;
    private readonly ITickerClock _clock;
    private readonly SchedulePolicyEvaluator _policies;
    private readonly ChainCoordinator _chains;
    private readonly TempusRuntimeState _state;
    private readonly IServiceProvider _services;
    private readonly ILogger<TimeTickerManager> _logger;

    public TimeTickerManager(ITickerPersistenceProvider provider, TickerFunctionRegistry registry, IOptions<TempusOptions> options, ITickerClock clock,
        SchedulePolicyEvaluator policies, ChainCoordinator chains, TempusRuntimeState state, IServiceProvider services, ILogger<TimeTickerManager> logger)
    {
        _provider = provider;
        _registry = registry;
        _options = options.Value;
        _clock = clock;
        _policies = policies;
        _chains = chains;
        _state = state;
        _services = services;
        _logger = logger;
    }

    private TempusHostScheduler? Scheduler => _services.GetService<TempusHostScheduler>();
    private void Wake() => Scheduler?.Restart();

    // ------------------------------------------------------------------ validation

    internal static List<TimeTickerEntity> Flatten(TimeTickerEntity root, string ns)
    {
        var all = new List<TimeTickerEntity>();
        var seen = new HashSet<Guid>();
        void Visit(TimeTickerEntity n, TimeTickerEntity? parent, int depth)
        {
            if (!seen.Add(n.Id)) throw new TempusException("A job appears more than once in the chain (cycle or shared node).");
            n.Namespace = ns;
            n.ParentId = parent?.Id;
            n.ChainRootId = parent is null ? null : root.Id;
            n.Depth = depth;
            if (parent is not null) n.ExecutionTime = null;
            if (n.IsGroup) n.Function = "$group";
            all.Add(n);
            foreach (var c in n.Children) Visit(c, n, depth + 1);
            n.Children = [];
        }
        Visit(root, null, 0);
        return all;
    }

    private async Task ValidateAsync(IReadOnlyList<TimeTickerEntity> jobs, CancellationToken ct)
    {
        foreach (var j in jobs)
        {
            if (j.IsGroup) continue;
            if (string.IsNullOrWhiteSpace(j.Function)) throw new TempusException("Function name is required.");
            if (!_registry.TryGet(j.Function, out var d)) throw new TempusException($"Function '{j.Function}' is not registered.");
            if (d.RequestType is not null && j.Request is not null)
            {
                try { TickerHelper.ReadTickerRequest(j.Request, d.RequestType); }
                catch (Exception ex) { throw new TempusException($"Request for '{j.Function}' does not match {d.RequestType.Name}: {ex.Message}", ex); }
            }
            if (j.Retries < 0) throw new TempusException("Retries cannot be negative.");
            if (j.ParentId is not null && j.SkipAfterSeconds is not null) throw new TempusException("WhenMissed can only be set on the chain root.");
            if (j.ParentId is not null && j.SchedulePolicyName is not null) throw new TempusException("A schedule policy can only be set on the chain root.");
            if (j.Priority == TickerTaskPriority.Normal && d.Priority != TickerTaskPriority.Normal) j.Priority = d.Priority;
        }
    }

    private async Task ApplyPolicyAsync(TimeTickerEntity root, CancellationToken ct)
    {
        if (root.SchedulePolicyName is null) return;
        var policy = await _provider.GetSchedulePolicyAsync(root.SchedulePolicyName, ct).ConfigureAwait(false)
            ?? throw new TempusException($"Schedule policy '{root.SchedulePolicyName}' does not exist.");
        root.SchedulePolicyRevision = policy.Revision;
        var requested = root.ExecutionTime ?? _clock.UtcNow;
        root.RequestedExecutionTime = requested;
        var eval = await _policies.EvaluateAsync(policy, requested, ct).ConfigureAwait(false);
        if (eval.Allowed) { root.ExecutionTime = requested; return; }
        switch (root.WhenBlocked)
        {
            case BlockedOccurrenceBehavior.Reject:
                throw new TempusException($"The requested time is blocked by policy '{policy.Name}': {eval.Reason}");
            case BlockedOccurrenceBehavior.Skip:
                root.ExecutionTime = requested;
                root.Status = TickerStatus.Skipped;
                root.SkipReason = $"Blocked by policy '{policy.Name}': {eval.Reason}";
                root.ExecutedAt = _clock.UtcNow;
                root.AdjustmentReason = root.SkipReason;
                break;
            case BlockedOccurrenceBehavior.MoveForward:
                var next = await _policies.NextAllowedAsync(policy.Name, requested, ct: ct).ConfigureAwait(false)
                    ?? throw new TempusException($"No allowed instant found within the search window for policy '{policy.Name}'.");
                root.ExecutionTime = next;
                root.AdjustmentReason = $"Moved forward from {requested:u} by policy '{policy.Name}': {eval.Reason}";
                break;
        }
    }

    private async Task<TickerResult<TimeTickerEntity>> AddGraphAsync(TimeTickerEntity root, CancellationToken ct)
    {
        try
        {
            if (root.ParentId is not null) throw new TempusException("AddAsync expects a root job (ParentId must be null).");
            root.ExecutionTime ??= _clock.UtcNow;
            if (root.ExecutionTime.Value.Kind == DateTimeKind.Local) root.ExecutionTime = root.ExecutionTime.Value.ToUniversalTime();
            root.ExecutionTime = DateTime.SpecifyKind(root.ExecutionTime.Value, DateTimeKind.Utc);
            var all = Flatten(root, _options.Namespace);
            await ValidateAsync(all, ct).ConfigureAwait(false);
            await ApplyPolicyAsync(root, ct).ConfigureAwait(false);
            if (root.Status == TickerStatus.Skipped)
                foreach (var j in all.Skip(1)) { j.Status = TickerStatus.Skipped; j.SkipReason = "Chain root was skipped by its schedule policy."; j.ExecutedAt = root.ExecutedAt; }
            var now = _clock.UtcNow;
            foreach (var j in all) { j.CreatedAt = now; j.UpdatedAt = now; }
            await _provider.InsertTimeTickersAsync(all, ct).ConfigureAwait(false);
            Wake();
            TimeChainBuilder.Nest(root, all);
            return TickerResult<TimeTickerEntity>.Success(root, all.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TickerResult<TimeTickerEntity>.Failure(ex);
        }
    }

    // ------------------------------------------------------------------ add

    public Task<TickerResult<TimeTickerEntity>> AddAsync(TimeTickerEntity entity, CancellationToken ct = default) => AddGraphAsync(entity, ct);

    public Task<TickerResult<TimeTickerEntity>> AddAsync<TFunction>(DateTime executionTimeUtc, CancellationToken ct = default) =>
        AddAsync<TFunction>(TimeSchedule.At(executionTimeUtc), ct);

    public Task<TickerResult<TimeTickerEntity>> AddAsync<TFunction>(TimeSchedule schedule, CancellationToken ct = default) =>
        AddGraphAsync(schedule.ToEntity(FunctionNaming.NameOf<TFunction>(), null), ct);

    public Task<TickerResult<TimeTickerEntity>> AddAsync<TFunction, TRequest>(DateTime executionTimeUtc, TRequest request, CancellationToken ct = default) =>
        AddAsync<TFunction, TRequest>(TimeSchedule.At(executionTimeUtc), request, ct);

    public Task<TickerResult<TimeTickerEntity>> AddAsync<TFunction, TRequest>(TimeSchedule schedule, TRequest request, CancellationToken ct = default) =>
        AddGraphAsync(schedule.ToEntity(FunctionNaming.NameOf<TFunction>(), TickerHelper.CreateTickerRequest(request)), ct);

    public Task<TickerResult<TimeTickerEntity>> AddChainAsync(Action<TimeChainBuilder> build, CancellationToken ct = default)
    {
        try
        {
            var b = new TimeChainBuilder();
            build(b);
            if (b.Root is not null && b.Root.Count() > 1000) return Task.FromResult(TickerResult<TimeTickerEntity>.Failure("A chain may contain at most 1000 jobs."));
            return AddGraphAsync(b.Build(), ct);
        }
        catch (Exception ex) { return Task.FromResult(TickerResult<TimeTickerEntity>.Failure(ex)); }
    }

    public async Task<TickerResult<IReadOnlyList<TimeTickerEntity>>> AddBatchAsync(IReadOnlyList<TimeTickerEntity> entities, CancellationToken ct = default)
    {
        var results = new List<TimeTickerEntity>();
        foreach (var e in entities)
        {
            var r = await AddGraphAsync(e, ct).ConfigureAwait(false);
            if (!r.IsSucceeded) return TickerResult<IReadOnlyList<TimeTickerEntity>>.Failure(r.Exception!);
            results.Add(r.Result!);
        }
        return TickerResult<IReadOnlyList<TimeTickerEntity>>.Success(results, results.Count);
    }

    public async Task<TimeTickerEntity> StageAsync(TimeTickerEntity entity, CancellationToken ct = default)
    {
        var staging = _services.GetService<ITickerStagingProvider>()
            ?? throw new TempusException("StageAsync requires the EF Core operational store with UseApplicationDbContext.");
        entity.ExecutionTime ??= _clock.UtcNow;
        var all = Flatten(entity, _options.Namespace);
        await ValidateAsync(all, ct).ConfigureAwait(false);
        await ApplyPolicyAsync(entity, ct).ConfigureAwait(false);
        await staging.StageTimeTickersAsync(all, ct).ConfigureAwait(false);
        TimeChainBuilder.Nest(entity, all);
        return entity;
    }

    // ------------------------------------------------------------------ update

    public async Task<TickerResult<TimeTickerEntity>> UpdateAsync(TimeTickerEntity entity, CancellationToken ct = default)
    {
        try
        {
            var current = await _provider.GetTimeTickerAsync(entity.Id, ct).ConfigureAwait(false) ?? throw new TempusException($"Job {entity.Id} was not found.");
            if (current.Status is TickerStatus.Queued or TickerStatus.InProgress) throw new TempusException("A queued or running job cannot be updated.");
            entity.Namespace = _options.Namespace;
            entity.ParentId = current.ParentId; entity.ChainRootId = current.ChainRootId; entity.Depth = current.Depth; entity.IsGroup = current.IsGroup; entity.JoinKind = current.JoinKind;
            entity.Children = [];
            await ValidateAsync([entity], ct).ConfigureAwait(false);
            if (entity.ParentId is null && entity.Status == TickerStatus.Idle)
            {
                entity.ExecutionTime ??= _clock.UtcNow;
                if (entity.SchedulePolicyName != current.SchedulePolicyName || entity.ExecutionTime != current.ExecutionTime)
                    await ApplyPolicyAsync(entity, ct).ConfigureAwait(false);
            }
            if (!await _provider.UpdateTimeTickerAsync(entity, ct).ConfigureAwait(false)) throw new TempusException($"Job {entity.Id} was not found.");
            Wake();
            return TickerResult<TimeTickerEntity>.Success(entity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<TimeTickerEntity>.Failure(ex); }
    }

    public async Task<TickerResult<IReadOnlyList<TimeTickerEntity>>> UpdateBatchAsync(IReadOnlyList<TimeTickerEntity> entities, CancellationToken ct = default)
    {
        var results = new List<TimeTickerEntity>();
        foreach (var e in entities)
        {
            var r = await UpdateAsync(e, ct).ConfigureAwait(false);
            if (!r.IsSucceeded) return TickerResult<IReadOnlyList<TimeTickerEntity>>.Failure(r.Exception!);
            results.Add(r.Result!);
        }
        return TickerResult<IReadOnlyList<TimeTickerEntity>>.Success(results, results.Count);
    }

    public async Task<TickerResult<TimeTickerEntity>> ReplaceChainAsync(Guid rootId, TimeTickerEntity newRoot, CancellationToken ct = default)
    {
        try
        {
            var chain = await _provider.GetChainAsync(rootId, ct).ConfigureAwait(false);
            if (chain.Count == 0) throw new TempusException($"Chain {rootId} was not found.");
            if (chain.Any(j => j.Status is TickerStatus.Queued or TickerStatus.InProgress)) throw new TempusException("The chain has running jobs and cannot be replaced.");
            await _provider.DeleteTimeTickersAsync(chain.Select(j => j.Id).ToList(), ct).ConfigureAwait(false);
            newRoot.Id = rootId;
            return await AddGraphAsync(newRoot, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<TimeTickerEntity>.Failure(ex); }
    }

    public async Task<TickerResult<TimeTickerEntity>> RescheduleAsync(Guid id, DateTime executionTimeUtc, CancellationToken ct = default)
    {
        try
        {
            var job = await _provider.GetTimeTickerAsync(id, ct).ConfigureAwait(false) ?? throw new TempusException($"Job {id} was not found.");
            if (job.ParentId is not null) throw new TempusException("Only root jobs can be rescheduled.");
            if (job.Status != TickerStatus.Idle) throw new TempusException($"Job {id} is {job.Status} and cannot be rescheduled.");
            job.ExecutionTime = DateTime.SpecifyKind(executionTimeUtc.Kind == DateTimeKind.Local ? executionTimeUtc.ToUniversalTime() : executionTimeUtc, DateTimeKind.Utc);
            job.RequestedExecutionTime = null; job.AdjustmentReason = null;
            await ApplyPolicyAsync(job, ct).ConfigureAwait(false);
            await _provider.UpdateTimeTickerAsync(job, ct).ConfigureAwait(false);
            Wake();
            return TickerResult<TimeTickerEntity>.Success(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<TimeTickerEntity>.Failure(ex); }
    }

    public async Task<TickerResult<TimeTickerEntity>> SetDeleteBehaviorAsync(Guid rootId, ChainDeleteBehavior behavior, CancellationToken ct = default)
    {
        try
        {
            var job = await _provider.GetTimeTickerAsync(rootId, ct).ConfigureAwait(false) ?? throw new TempusException($"Job {rootId} was not found.");
            job.DeleteBehavior = behavior;
            await _provider.UpdateTimeTickerAsync(job, ct).ConfigureAwait(false);
            return TickerResult<TimeTickerEntity>.Success(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<TimeTickerEntity>.Failure(ex); }
    }

    // ------------------------------------------------------------------ run / retry

    public async Task<TickerResult<TimeTickerEntity>> RunNowAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            if (_options.BackgroundServicesDisabled) throw new TempusException("RunNow requires a host with background services enabled.");
            var job = await _provider.GetTimeTickerAsync(id, ct).ConfigureAwait(false) ?? throw new TempusException($"Job {id} was not found.");
            if (job.ParentId is not null) throw new TempusException("Only root jobs can be run on demand. Use RetryFromAsync for chain steps.");
            if (job.Status is TickerStatus.Queued or TickerStatus.InProgress) throw new TempusException("The job is already running.");
            if (job.Status.IsTerminal())
            {
                var chain = await _provider.GetChainAsync(job.Id, ct).ConfigureAwait(false);
                if (chain.Count > 1)
                {
                    var root = await _chains.RetryFromAsync(job.Id, _options.Retention.MaxNodesPerChain, 50, ct).ConfigureAwait(false);
                    Wake();
                    return TickerResult<TimeTickerEntity>.Success(root);
                }
                job.ClearOutcome();
                job.Generation++;
            }
            job.Status = TickerStatus.Idle;
            job.ExecutionTime = _clock.UtcNow;
            job.SkipAfterSeconds = null;
            await _provider.UpdateTimeTickerAsync(job, ct).ConfigureAwait(false);
            Wake();
            return TickerResult<TimeTickerEntity>.Success(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<TimeTickerEntity>.Failure(ex); }
    }

    public async Task<TickerResult<TimeTickerEntity>> RetryFromAsync(Guid stepId, CancellationToken ct = default)
    {
        try
        {
            if (_options.BackgroundServicesDisabled) throw new TempusException("RetryFrom requires a host with background services enabled.");
            var root = await _chains.RetryFromAsync(stepId, _options.Retention.MaxNodesPerChain, 50, ct).ConfigureAwait(false);
            Wake();
            return TickerResult<TimeTickerEntity>.Success(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<TimeTickerEntity>.Failure(ex); }
    }

    // ------------------------------------------------------------------ cancel

    public async Task<TickerResult<TimeTickerEntity>> CancelAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var job = await _provider.GetTimeTickerAsync(id, ct).ConfigureAwait(false) ?? throw new TempusException($"Job {id} was not found.");
            if (!job.Status.IsPending()) throw new TempusException($"Job {id} is {job.Status}; only Idle or Queued jobs can be cancelled. Use RequestCancellationAsync for running work.");
            if (job.Status == TickerStatus.Queued && _state.TryCancelLocal(job.Id)) { }
            var chainIds = job.ChainRootId is null && !job.IsRoot ? [] : (await _provider.GetChainAsync(job.ChainRootId ?? job.Id, ct).ConfigureAwait(false)).ToList();
            var now = _clock.UtcNow;
            var updates = new List<TimeTickerEntity>();
            void Cancel(TimeTickerEntity j, string reason)
            {
                if (!j.Status.IsPending() && !(j.IsGroup && j.Status == TickerStatus.InProgress)) return;
                j.Status = TickerStatus.Cancelled; j.ExecutedAt = now; j.Exception = reason; j.LockHolder = null; j.LeaseUntil = null;
                updates.Add(j);
                foreach (var c in chainIds.Where(c => c.ParentId == j.Id)) Cancel(c, "Cancelled with its parent.");
            }
            var target = chainIds.FirstOrDefault(c => c.Id == job.Id) ?? job;
            Cancel(target, "Cancelled by request.");
            await _provider.UpdateTimeTickersAsync(updates, ct).ConfigureAwait(false);
            await _chains.OnTerminalAsync(target, ct).ConfigureAwait(false);
            return TickerResult<TimeTickerEntity>.Success(target, updates.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<TimeTickerEntity>.Failure(ex); }
    }

    public Task<TickerResult<int>> CancelPendingAsync<TFunction>(CancellationToken ct = default) => CancelPendingAsync(FunctionNaming.NameOf<TFunction>(), ct);

    public async Task<TickerResult<int>> CancelPendingAsync(string function, CancellationToken ct = default)
    {
        try
        {
            var pending = await _provider.ListTimeTickersAsync(t => t.Function == function && (t.Status == TickerStatus.Idle || t.Status == TickerStatus.Queued), null, ct).ConfigureAwait(false);
            var n = 0;
            foreach (var p in pending)
            {
                var r = await CancelAsync(p.Id, ct).ConfigureAwait(false);
                if (r.IsSucceeded) n += r.AffectedRows;
            }
            return TickerResult<int>.Success(n, n);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<int>.Failure(ex); }
    }

    public async Task<TickerResult<int>> RequestCancellationAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var job = await _provider.GetTimeTickerAsync(id, ct).ConfigureAwait(false) ?? throw new TempusException($"Job {id} was not found.");
            if (job.Status != TickerStatus.InProgress && job.Status != TickerStatus.Queued) return TickerResult<int>.Success(0, 0);
            await _provider.MarkCancellationRequestedAsync(TickerType.TimeTicker, id, ct).ConfigureAwait(false);
            if (_state.TryCancelLocal(id)) return TickerResult<int>.Success(1, 1);
            if (job.LockHolder is null) return TickerResult<int>.Success(0, 0);
            await _provider.InsertControlCommandAsync(new ControlCommandEntity
            {
                Namespace = _options.Namespace, TargetOwnerId = job.LockHolder, Command = "cancel", TickerType = TickerType.TimeTicker,
                TickerId = id, AcquisitionToken = job.AcquisitionToken, CreatedAt = _clock.UtcNow, ExpiresAt = _clock.UtcNow.AddSeconds(30),
            }, ct).ConfigureAwait(false);
            return TickerResult<int>.Success(1, 1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<int>.Failure(ex); }
    }

    // ------------------------------------------------------------------ outcome

    public async Task<ExecutionOutcome?> GetOutcomeAsync(Guid id, CancellationToken ct = default)
    {
        var job = await _provider.GetTimeTickerAsync(id, ct).ConfigureAwait(false);
        return job is null ? null : ToOutcome(job);
    }

    internal static ExecutionOutcome ToOutcome(TickerExecutionEntity e) => new()
    {
        Id = e.Id, Type = e.TickerType, Status = e.Status, HasResult = e.HasResult, ResultJson = e.Result, Exception = e.Exception,
        SkipReason = e.SkipReason, ExecutedAt = e.ExecutedAt, ElapsedMilliseconds = e.ElapsedMilliseconds, RetryCount = e.RetryCount,
    };

    public async Task<ExecutionOutcome?> WaitForCompletionAsync(Guid id, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var o = await GetOutcomeAsync(id, ct).ConfigureAwait(false);
            if (o is null) return null;
            if (o.IsTerminal) return o;
            if (DateTime.UtcNow >= deadline) return o;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ read

    public Task<TimeTickerEntity?> GetAsync(Guid id, CancellationToken ct = default) => _provider.GetTimeTickerAsync(id, ct);
    public Task<IReadOnlyList<TimeTickerEntity>> GetChainAsync(Guid rootId, CancellationToken ct = default) => _provider.GetChainAsync(rootId, ct);
    public Task<IReadOnlyList<TimeTickerEntity>> ListAsync(CancellationToken ct = default) => _provider.ListTimeTickersAsync(null, null, ct);
    public Task<IReadOnlyList<TimeTickerEntity>> ListAsync(Expression<Func<TimeTickerEntity, bool>> filter, CancellationToken ct = default) => _provider.ListTimeTickersAsync(filter, null, ct);
    public Task<IReadOnlyList<TimeTickerEntity>> ListAsync<TFunction>(CancellationToken ct = default)
    {
        var name = FunctionNaming.NameOf<TFunction>();
        return _provider.ListTimeTickersAsync(t => t.Function == name, null, ct);
    }

    // ------------------------------------------------------------------ delete

    public async Task<TickerResult<int>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var job = await _provider.GetTimeTickerAsync(id, ct).ConfigureAwait(false) ?? throw new TempusException($"Job {id} was not found.");
            if (job.Status is TickerStatus.Queued or TickerStatus.InProgress) throw new TempusException("A queued or running job cannot be deleted. Cancel it first.");
            var rootId = job.ChainRootId ?? job.Id;
            var chain = await _provider.GetChainAsync(rootId, ct).ConfigureAwait(false);
            var root = chain.FirstOrDefault(c => c.Id == rootId) ?? job;
            if (root.DeleteBehavior == ChainDeleteBehavior.Restrict && chain.Any(c => !c.Status.IsTerminal() && c.Id != job.Id))
                throw new TempusException("Deletion is restricted while unfinished descendants exist.");
            var ids = job.ParentId is null ? chain.Select(c => c.Id).ToList() : Descendants(job.Id, chain).Append(job.Id).ToList();
            var n = await _provider.DeleteTimeTickersAsync(ids, ct).ConfigureAwait(false);
            return TickerResult<int>.Success(n, n);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<int>.Failure(ex); }
    }

    private static IEnumerable<Guid> Descendants(Guid id, IReadOnlyList<TimeTickerEntity> chain)
    {
        foreach (var c in chain.Where(c => c.ParentId == id))
        {
            yield return c.Id;
            foreach (var d in Descendants(c.Id, chain)) yield return d;
        }
    }

    public async Task<TickerResult<int>> DeleteAsync(Expression<Func<TimeTickerEntity, bool>> filter, CancellationToken ct = default)
    {
        try
        {
            var n = await _provider.DeleteTimeTickersAsync(filter, ct).ConfigureAwait(false);
            return TickerResult<int>.Success(n, n);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<int>.Failure(ex); }
    }

    public async Task<TickerResult<int>> DeleteBatchAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var n = 0;
        foreach (var id in ids)
        {
            var r = await DeleteAsync(id, ct).ConfigureAwait(false);
            if (!r.IsSucceeded) return r;
            n += r.AffectedRows;
        }
        return TickerResult<int>.Success(n, n);
    }
}
