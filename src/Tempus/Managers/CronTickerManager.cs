using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tempus.Entities;
using Tempus.Functions;
using Tempus.Options;
using Tempus.Persistence;
using Tempus.Scheduling;

namespace Tempus.Managers;

public sealed class CronTickerManager : ICronTickerManager<CronTickerEntity>
{
    private readonly ITickerPersistenceProvider _provider;
    private readonly TickerFunctionRegistry _registry;
    private readonly TempusOptions _options;
    private readonly ITickerClock _clock;
    private readonly CronPlanner _planner;
    private readonly TempusRuntimeState _state;
    private readonly IServiceProvider _services;

    public CronTickerManager(ITickerPersistenceProvider provider, TickerFunctionRegistry registry, IOptions<TempusOptions> options, ITickerClock clock, CronPlanner planner, TempusRuntimeState state, IServiceProvider services)
    {
        _provider = provider;
        _registry = registry;
        _options = options.Value;
        _clock = clock;
        _planner = planner;
        _state = state;
        _services = services;
    }

    private void Wake() => _services.GetService<TempusHostScheduler>()?.Restart();

    private async Task ValidateAsync(CronTickerEntity e, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(e.Function)) throw new TempusException("Function name is required.");
        if (!_registry.TryGet(e.Function, out var d)) throw new TempusException($"Function '{e.Function}' is not registered.");
        CronParser.Parse(e.Expression);
        if (e.TimeZoneId is not null) Policies.SchedulePolicyEvaluator.ResolveTimeZone(e.TimeZoneId);
        if (d.RequestType is not null && e.Request is not null)
        {
            try { TickerHelper.ReadTickerRequest(e.Request, d.RequestType); }
            catch (Exception ex) { throw new TempusException($"Request for '{e.Function}' does not match {d.RequestType.Name}: {ex.Message}", ex); }
        }
        if (e.CatchUpMode == CronCatchUpMode.RunLatest && e.SkipAfterSeconds is null)
            throw new TempusException("CatchUp(RunLatest) requires WhenMissed(SkipAfter(...)) so the window is bounded.");
        if (e.SchedulePolicyName is not null && await _provider.GetSchedulePolicyAsync(e.SchedulePolicyName, ct).ConfigureAwait(false) is null)
            throw new TempusException($"Schedule policy '{e.SchedulePolicyName}' does not exist.");
        if (e.ChainTemplateJson is not null)
        {
            var template = e.Chain!;
            if (template.Children.Count == 0) throw new TempusException("A chain template needs at least one child.");
            var count = 1; var depth = 0;
            void Visit(CronChainNode n, int d)
            {
                count++; depth = Math.Max(depth, d);
                if (!n.IsGroup && !_registry.Contains(n.Function)) throw new TempusException($"Function '{n.Function}' in the chain template is not registered.");
                foreach (var c in n.Children) Visit(c, d + 1);
            }
            foreach (var c in template.Children) Visit(c, 1);
            if (count > 500) throw new TempusException("A recurring chain may contain at most 500 jobs.");
            if (depth > 20) throw new TempusException("A recurring chain may have at most 20 child levels.");
            if (!_provider.SupportsCronChains) throw new TempusException("Recurring chains require a provider with atomic chain publication support.");
        }
        if (e.Priority == TickerTaskPriority.Normal && d.Priority != TickerTaskPriority.Normal) e.Priority = d.Priority;
    }

    public async Task<TickerResult<CronTickerEntity>> AddAsync(CronTickerEntity entity, CancellationToken ct = default)
    {
        try
        {
            entity.Namespace = _options.Namespace;
            await ValidateAsync(entity, ct).ConfigureAwait(false);
            entity.CreatedAt = entity.UpdatedAt = _clock.UtcNow;
            entity.NextRunAt = await _planner.NextAllowedAsync(entity, _clock.UtcNow, ct).ConfigureAwait(false);
            await _provider.InsertCronTickerAsync(entity, ct).ConfigureAwait(false);
            if (entity.Enabled) await _planner.PlanCronAsync(entity, _clock.UtcNow, false, ct).ConfigureAwait(false);
            Wake();
            return TickerResult<CronTickerEntity>.Success(entity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<CronTickerEntity>.Failure(ex); }
    }

    public Task<TickerResult<CronTickerEntity>> AddAsync<TFunction>(CronSchedule schedule, CancellationToken ct = default) => AddAsync(schedule.ToEntity(FunctionNaming.NameOf<TFunction>()), ct);

    public Task<TickerResult<CronTickerEntity>> AddAsync<TFunction, TRequest>(CronSchedule schedule, TRequest request, CancellationToken ct = default)
    {
        var e = schedule.ToEntity(FunctionNaming.NameOf<TFunction>());
        e.Request = TickerHelper.CreateTickerRequest(request);
        return AddAsync(e, ct);
    }

    public Task<TickerResult<CronTickerEntity>> AddChainAsync(Action<CronChainBuilder> build, CancellationToken ct = default)
    {
        try
        {
            var b = new CronChainBuilder();
            build(b);
            return AddAsync(b.Build(), ct);
        }
        catch (Exception ex) { return Task.FromResult(TickerResult<CronTickerEntity>.Failure(ex)); }
    }

    public async Task<TickerResult<IReadOnlyList<CronTickerEntity>>> AddBatchAsync(IReadOnlyList<CronTickerEntity> entities, CancellationToken ct = default)
    {
        var list = new List<CronTickerEntity>();
        foreach (var e in entities)
        {
            var r = await AddAsync(e, ct).ConfigureAwait(false);
            if (!r.IsSucceeded) return TickerResult<IReadOnlyList<CronTickerEntity>>.Failure(r.Exception!);
            list.Add(r.Result!);
        }
        return TickerResult<IReadOnlyList<CronTickerEntity>>.Success(list, list.Count);
    }

    public async Task<CronTickerEntity> StageAsync(CronTickerEntity entity, CancellationToken ct = default)
    {
        var staging = _services.GetService<ITickerStagingProvider>() ?? throw new TempusException("StageAsync requires the EF Core operational store with UseApplicationDbContext.");
        entity.Namespace = _options.Namespace;
        await ValidateAsync(entity, ct).ConfigureAwait(false);
        entity.NextRunAt = await _planner.NextAllowedAsync(entity, _clock.UtcNow, ct).ConfigureAwait(false);
        await staging.StageCronTickerAsync(entity, ct).ConfigureAwait(false);
        return entity;
    }

    public async Task<TickerResult<CronTickerEntity>> UpdateAsync(CronTickerEntity entity, CancellationToken ct = default)
    {
        try
        {
            var current = await _provider.GetCronTickerAsync(entity.Id, ct).ConfigureAwait(false) ?? throw new TempusException($"Cron {entity.Id} was not found.");
            entity.Namespace = _options.Namespace;
            entity.SeedKey = current.SeedKey;
            entity.CreatedAt = current.CreatedAt;
            await ValidateAsync(entity, ct).ConfigureAwait(false);
            var schedulingChanged = current.Expression != entity.Expression || current.TimeZoneId != entity.TimeZoneId || current.SchedulePolicyName != entity.SchedulePolicyName
                || current.Function != entity.Function || current.Request != entity.Request || current.ChainTemplateJson != entity.ChainTemplateJson;
            entity.DefinitionVersion = schedulingChanged ? current.DefinitionVersion + 1 : current.DefinitionVersion;
            entity.UpdatedAt = _clock.UtcNow;
            entity.NextRunAt = entity.Enabled ? await _planner.NextAllowedAsync(entity, _clock.UtcNow, ct).ConfigureAwait(false) : null;
            await _provider.UpdateCronTickerAsync(entity, ct).ConfigureAwait(false);
            if (schedulingChanged)
            {
                var id = entity.Id; var version = entity.DefinitionVersion;
                await _provider.DeleteOccurrencesAsync(o => o.CronTickerId == id && o.Status == TickerStatus.Idle && !o.IsManual && o.DefinitionVersion < version, ct).ConfigureAwait(false);
            }
            if (entity.Enabled) await _planner.PlanCronAsync(entity, _clock.UtcNow, false, ct).ConfigureAwait(false);
            Wake();
            return TickerResult<CronTickerEntity>.Success(entity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<CronTickerEntity>.Failure(ex); }
    }

    public async Task<TickerResult<IReadOnlyList<CronTickerEntity>>> UpdateBatchAsync(IReadOnlyList<CronTickerEntity> entities, CancellationToken ct = default)
    {
        var list = new List<CronTickerEntity>();
        foreach (var e in entities)
        {
            var r = await UpdateAsync(e, ct).ConfigureAwait(false);
            if (!r.IsSucceeded) return TickerResult<IReadOnlyList<CronTickerEntity>>.Failure(r.Exception!);
            list.Add(r.Result!);
        }
        return TickerResult<IReadOnlyList<CronTickerEntity>>.Success(list, list.Count);
    }

    public async Task<TickerResult<CronTickerEntity>> SetEnabledAsync(Guid cronId, bool enabled, CancellationToken ct = default)
    {
        try
        {
            var cron = await _provider.GetCronTickerAsync(cronId, ct).ConfigureAwait(false) ?? throw new TempusException($"Cron {cronId} was not found.");
            cron.Enabled = enabled;
            cron.UpdatedAt = _clock.UtcNow;
            if (!enabled)
            {
                cron.NextRunAt = null;
                await _provider.DeleteOccurrencesAsync(o => o.CronTickerId == cronId && o.Status == TickerStatus.Idle && !o.IsManual, ct).ConfigureAwait(false);
            }
            await _provider.UpdateCronTickerAsync(cron, ct).ConfigureAwait(false);
            if (enabled) await _planner.PlanCronAsync(cron, _clock.UtcNow, false, ct).ConfigureAwait(false);
            Wake();
            return TickerResult<CronTickerEntity>.Success(cron);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<CronTickerEntity>.Failure(ex); }
    }

    public async Task<TickerResult<CronTickerOccurrenceEntity>> RunNowAsync(Guid cronId, CancellationToken ct = default)
    {
        try
        {
            if (_options.BackgroundServicesDisabled) throw new TempusException("RunNow requires a host with background services enabled.");
            var cron = await _provider.GetCronTickerAsync(cronId, ct).ConfigureAwait(false) ?? throw new TempusException($"Cron {cronId} was not found.");
            var occ = _planner.NewOccurrence(cron, _clock.UtcNow, manual: true);
            await _provider.TryInsertOccurrenceAsync(occ, ct).ConfigureAwait(false);
            Wake();
            return TickerResult<CronTickerOccurrenceEntity>.Success(occ);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<CronTickerOccurrenceEntity>.Failure(ex); }
    }

    public async Task<TickerResult<CronTickerOccurrenceEntity>> RerunOccurrenceAsync(Guid occurrenceId, CancellationToken ct = default)
    {
        try
        {
            var occ = await _provider.GetOccurrenceAsync(occurrenceId, ct).ConfigureAwait(false) ?? throw new TempusException($"Occurrence {occurrenceId} was not found.");
            if (!occ.Status.IsTerminal()) throw new TempusException($"Occurrence {occurrenceId} is {occ.Status} and cannot be re-run yet.");
            if (occ.ChainRootId is not null)
            {
                var chains = _services.GetRequiredService<ChainCoordinator>();
                await chains.RetryFromAsync(occ.ChainRootId.Value, _options.Retention.MaxNodesPerChain, 50, ct).ConfigureAwait(false);
                Wake();
                return TickerResult<CronTickerOccurrenceEntity>.Success(occ);
            }
            var cron = await _provider.GetCronTickerAsync(occ.CronTickerId, ct).ConfigureAwait(false);
            occ.ClearOutcome();
            occ.Generation++;
            occ.Status = TickerStatus.Idle;
            occ.ExecutionTime = _clock.UtcNow;
            occ.SkipAfterSeconds = null;
            occ.IsManual = true;
            if (cron is not null) occ.DefinitionVersion = cron.DefinitionVersion;
            await _provider.UpdateOccurrenceAsync(occ, ct).ConfigureAwait(false);
            Wake();
            return TickerResult<CronTickerOccurrenceEntity>.Success(occ);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<CronTickerOccurrenceEntity>.Failure(ex); }
    }

    public async Task<TickerResult<int>> RequestCancellationAsync(Guid occurrenceId, CancellationToken ct = default)
    {
        try
        {
            var occ = await _provider.GetOccurrenceAsync(occurrenceId, ct).ConfigureAwait(false) ?? throw new TempusException($"Occurrence {occurrenceId} was not found.");
            if (occ.Status is not (TickerStatus.InProgress or TickerStatus.Queued)) return TickerResult<int>.Success(0, 0);
            await _provider.MarkCancellationRequestedAsync(TickerType.CronTickerOccurrence, occurrenceId, ct).ConfigureAwait(false);
            if (_state.TryCancelLocal(occurrenceId)) return TickerResult<int>.Success(1, 1);
            if (occ.LockHolder is null) return TickerResult<int>.Success(0, 0);
            await _provider.InsertControlCommandAsync(new ControlCommandEntity
            {
                Namespace = _options.Namespace, TargetOwnerId = occ.LockHolder, TickerType = TickerType.CronTickerOccurrence, TickerId = occurrenceId,
                AcquisitionToken = occ.AcquisitionToken, CreatedAt = _clock.UtcNow, ExpiresAt = _clock.UtcNow.AddSeconds(30),
            }, ct).ConfigureAwait(false);
            return TickerResult<int>.Success(1, 1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<int>.Failure(ex); }
    }

    public async Task<TickerResult<CronTickerOccurrenceEntity>> CancelOccurrenceAsync(Guid occurrenceId, CancellationToken ct = default)
    {
        try
        {
            var occ = await _provider.GetOccurrenceAsync(occurrenceId, ct).ConfigureAwait(false) ?? throw new TempusException($"Occurrence {occurrenceId} was not found.");
            if (!occ.Status.IsPending()) throw new TempusException($"Occurrence {occurrenceId} is {occ.Status}; only pending occurrences can be cancelled.");
            _state.TryCancelLocal(occ.Id);
            occ.Status = TickerStatus.Cancelled; occ.ExecutedAt = _clock.UtcNow; occ.Exception = "Cancelled by request."; occ.LockHolder = null; occ.LeaseUntil = null;
            await _provider.UpdateOccurrenceAsync(occ, ct).ConfigureAwait(false);
            return TickerResult<CronTickerOccurrenceEntity>.Success(occ);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<CronTickerOccurrenceEntity>.Failure(ex); }
    }

    public async Task<IReadOnlyList<DateTime>> GetNextOccurrencesAsync(Guid cronId, int count, CancellationToken ct = default)
    {
        var cron = await _provider.GetCronTickerAsync(cronId, ct).ConfigureAwait(false) ?? throw new TempusException($"Cron {cronId} was not found.");
        return await _planner.NextOccurrencesAsync(cron, _clock.UtcNow, count, ct).ConfigureAwait(false);
    }

    public async Task<ExecutionOutcome?> GetOutcomeAsync(Guid occurrenceId, CancellationToken ct = default)
    {
        var occ = await _provider.GetOccurrenceAsync(occurrenceId, ct).ConfigureAwait(false);
        return occ is null ? null : TimeTickerManager.ToOutcome(occ);
    }

    public async Task<ExecutionOutcome?> WaitForCompletionAsync(Guid occurrenceId, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var o = await GetOutcomeAsync(occurrenceId, ct).ConfigureAwait(false);
            if (o is null) return null;
            if (o.IsTerminal || DateTime.UtcNow >= deadline) return o;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    public Task<CronTickerEntity?> GetAsync(Guid cronId, CancellationToken ct = default) => _provider.GetCronTickerAsync(cronId, ct);
    public Task<CronTickerOccurrenceEntity?> GetOccurrenceAsync(Guid occurrenceId, CancellationToken ct = default) => _provider.GetOccurrenceAsync(occurrenceId, ct);
    public Task<IReadOnlyList<CronTickerOccurrenceEntity>> ListOccurrencesAsync(Guid cronId, int take = 100, CancellationToken ct = default) => _provider.ListOccurrencesAsync(o => o.CronTickerId == cronId, take, ct);
    public Task<IReadOnlyList<CronTickerEntity>> ListAsync(CancellationToken ct = default) => _provider.ListCronTickersAsync(null, ct);
    public Task<IReadOnlyList<CronTickerEntity>> ListAsync(Expression<Func<CronTickerEntity, bool>> filter, CancellationToken ct = default) => _provider.ListCronTickersAsync(filter, ct);
    public Task<IReadOnlyList<CronTickerEntity>> ListAsync<TFunction>(CancellationToken ct = default)
    {
        var name = FunctionNaming.NameOf<TFunction>();
        return _provider.ListCronTickersAsync(c => c.Function == name, ct);
    }

    public async Task<TickerResult<int>> DeleteAsync(Guid cronId, CancellationToken ct = default)
    {
        try
        {
            var n = await _provider.DeleteCronTickersAsync([cronId], ct).ConfigureAwait(false);
            return TickerResult<int>.Success(n, n);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<int>.Failure(ex); }
    }

    public async Task<TickerResult<int>> DeleteAsync(Expression<Func<CronTickerEntity, bool>> filter, CancellationToken ct = default)
    {
        try
        {
            var n = await _provider.DeleteCronTickersAsync(filter, ct).ConfigureAwait(false);
            return TickerResult<int>.Success(n, n);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<int>.Failure(ex); }
    }

    public async Task<TickerResult<int>> DeleteBatchAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        try
        {
            var n = await _provider.DeleteCronTickersAsync(ids, ct).ConfigureAwait(false);
            return TickerResult<int>.Success(n, n);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<int>.Failure(ex); }
    }

    public async Task<TickerResult<int>> DeleteOccurrenceAsync(Guid occurrenceId, CancellationToken ct = default)
    {
        try
        {
            var occ = await _provider.GetOccurrenceAsync(occurrenceId, ct).ConfigureAwait(false) ?? throw new TempusException($"Occurrence {occurrenceId} was not found.");
            if (occ.Status is TickerStatus.Queued or TickerStatus.InProgress) throw new TempusException("A running occurrence cannot be deleted.");
            var n = await _provider.DeleteOccurrencesAsync([occurrenceId], ct).ConfigureAwait(false);
            return TickerResult<int>.Success(n, n);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<int>.Failure(ex); }
    }
}

public sealed class SchedulePolicyManager : ISchedulePolicyManager
{
    private readonly ITickerPersistenceProvider _provider;
    private readonly Policies.SchedulePolicyEvaluator _evaluator;
    private readonly TempusOptions _options;
    private readonly ITickerClock _clock;

    public SchedulePolicyManager(ITickerPersistenceProvider provider, Policies.SchedulePolicyEvaluator evaluator, IOptions<TempusOptions> options, ITickerClock clock)
    {
        _provider = provider;
        _evaluator = evaluator;
        _options = options.Value;
        _clock = clock;
    }

    public Task<IReadOnlyList<SchedulePolicyEntity>> ListAsync(CancellationToken ct = default) => _provider.ListSchedulePoliciesAsync(ct);
    public Task<SchedulePolicyEntity?> GetAsync(string name, CancellationToken ct = default) => _provider.GetSchedulePolicyAsync(name, ct);

    public async Task<TickerResult<SchedulePolicyEntity>> UpsertAsync(SchedulePolicyEntity policy, CancellationToken ct = default)
    {
        try
        {
            policy.Namespace = _options.Namespace;
            var existing = await _provider.GetSchedulePolicyAsync(policy.Name, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if (policy.Revision != existing.Revision) throw new TempusException($"Policy '{policy.Name}' was modified (revision {existing.Revision}); reload it and retry.");
                policy.Id = existing.Id;
                policy.CreatedAt = existing.CreatedAt;
                policy.Revision = existing.Revision + 1;
                policy.IsSeeded = existing.IsSeeded;
            }
            else policy.Revision = 1;
            await _evaluator.ValidateAsync(policy, ct).ConfigureAwait(false);
            policy.UpdatedAt = _clock.UtcNow;
            await _provider.UpsertSchedulePolicyAsync(policy, ct).ConfigureAwait(false);
            return TickerResult<SchedulePolicyEntity>.Success(policy);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<SchedulePolicyEntity>.Failure(ex); }
    }

    public async Task<TickerResult<int>> DeleteAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var refs = await _provider.CountSchedulePolicyReferencesAsync(name, ct).ConfigureAwait(false);
            if (refs > 0) throw new TempusException($"Policy '{name}' is referenced by {refs} job(s), cron(s) or policies and cannot be deleted.");
            var ok = await _provider.DeleteSchedulePolicyAsync(name, ct).ConfigureAwait(false);
            return TickerResult<int>.Success(ok ? 1 : 0, ok ? 1 : 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TickerResult<int>.Failure(ex); }
    }

    public Task<Policies.PolicyEvaluation> EvaluateAsync(string name, DateTime utcInstant, CancellationToken ct = default) => _evaluator.EvaluateAsync(name, utcInstant, ct);

    public async Task<IReadOnlyList<(DateTime Utc, bool Allowed, string? Reason)>> PreviewAsync(string name, string cronExpression, string? timeZoneId, int count = 20, CancellationToken ct = default)
    {
        var tz = timeZoneId is null ? _options.Scheduler.SchedulerTimeZone : Policies.SchedulePolicyEvaluator.ResolveTimeZone(timeZoneId);
        var list = new List<(DateTime, bool, string?)>();
        var cursor = _clock.UtcNow;
        for (var i = 0; i < count; i++)
        {
            var next = CronParser.GetNextOccurrence(cronExpression, cursor, tz);
            if (next is null) break;
            var eval = await _evaluator.EvaluateAsync(name, next.Value, ct).ConfigureAwait(false);
            list.Add((next.Value, eval.Allowed, eval.Reason));
            cursor = next.Value;
        }
        return list;
    }
}
