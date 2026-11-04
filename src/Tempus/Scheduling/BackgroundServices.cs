using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tempus.Entities;
using Tempus.Functions;
using Tempus.Instrumentation;
using Tempus.Logging;
using Tempus.Options;
using Tempus.Persistence;

namespace Tempus.Scheduling;

/// <summary>Runs seeding once at startup, before the scheduler starts.</summary>
public sealed class TempusStartupService : IHostedService
{
    private readonly TempusSeeder _seeder;
    private readonly TickerFunctionRegistry _registry;
    private readonly IEnumerable<ITempusStoreInitializer> _initializers;
    private readonly TempusOptions _options;
    private readonly ILogger<TempusStartupService> _logger;

    public TempusStartupService(TempusSeeder seeder, TickerFunctionRegistry registry, IEnumerable<ITempusStoreInitializer> initializers, IOptions<TempusOptions> options, ILogger<TempusStartupService> logger)
    {
        _initializers = initializers;
        _options = options.Value;
        _seeder = seeder;
        _registry = registry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var initializer in _initializers) await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_options.Namespace == "default" && _options.ProviderFactory is not null)
            _logger.LogWarning("Tempus is using the namespace 'default' with a persistent store. Set options.UseApplicationNamespace(...) to a value unique to this application, especially when several applications share one database.");
        _logger.LogInformation("Tempus registered {Count} function(s): {Functions}", _registry.Functions.Count, string.Join(", ", _registry.Functions.Select(f => f.Name)));
        await _seeder.SeedAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Lease renewal for local executions plus the stale-job watchdog.</summary>
public sealed class LeaseRecoveryService : BackgroundService
{
    private readonly ITickerPersistenceProvider _provider;
    private readonly TempusOptions _options;
    private readonly TempusRuntimeState _state;
    private readonly ITickerClock _clock;
    private readonly ChainCoordinator _chains;
    private readonly IServiceProvider _services;
    private readonly ILogger<LeaseRecoveryService> _logger;

    public LeaseRecoveryService(ITickerPersistenceProvider provider, IOptions<TempusOptions> options, TempusRuntimeState state, ITickerClock clock, ChainCoordinator chains, IServiceProvider services, ILogger<LeaseRecoveryService> logger)
    {
        _provider = provider;
        _options = options.Value;
        _state = state;
        _clock = clock;
        _chains = chains;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Scheduler.StaleJobRecoveryEnabled) return;
        if (!_provider.SupportsLeaseBasedRecovery)
        {
            _logger.LogWarning("The persistence provider does not support lease-based recovery: lease renewal and the stale-job watchdog are disabled for this node.");
            return;
        }
        var renew = Task.Run(() => RenewLoopAsync(stoppingToken), stoppingToken);
        var watchdog = Task.Run(() => WatchdogLoopAsync(stoppingToken), stoppingToken);
        await Task.WhenAll(renew, watchdog).ConfigureAwait(false);
    }

    private async Task RenewLoopAsync(CancellationToken ct)
    {
        var s = _options.Scheduler;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(s.LeaseRenewalInterval, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            var leaseUntil = _clock.UtcNow + s.LeaseDuration;
            foreach (var run in _state.Running.Values.ToList())
            {
                if (run.Token == Guid.Empty) continue;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(s.LeaseRenewalInterval);
                    var ok = await _provider.RenewLeaseAsync(run.Type, run.Id, run.Token, leaseUntil, cts.Token).ConfigureAwait(false);
                    if (!ok) _logger.LogWarning("Lease renewal for {Id} was rejected (ownership lost).", run.Id);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Lease renewal failed for {Id}.", run.Id); }
            }
        }
    }

    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        var s = _options.Scheduler;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(s.StaleWatchdogInterval, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            try
            {
                var now = _clock.UtcNow;
                var staleTime = await _provider.FindExpiredTimeTickerLeasesAsync(now, 100, ct).ConfigureAwait(false);
                foreach (var t in staleTime) await RecoverAsync(t, now, ct).ConfigureAwait(false);
                var staleOcc = await _provider.FindExpiredOccurrenceLeasesAsync(now, 100, ct).ConfigureAwait(false);
                foreach (var o in staleOcc) await RecoverAsync(o, now, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Stale watchdog sweep failed."); }
        }
    }

    private async Task RecoverAsync(TickerExecutionEntity item, DateTime now, CancellationToken ct)
    {
        if (_state.Running.ContainsKey(item.Id)) return; // ours and still alive: renewal will catch up
        var token = item.AcquisitionToken ?? Guid.Empty;
        var max = item.MaxStaleRestarts ?? _options.Scheduler.MaxStaleRestarts;
        var restart = item.OnStale == StaleAction.Restart && item.StaleRestartCount < max;
        if (restart)
        {
            item.StaleRestartCount++;
            item.Status = TickerStatus.Idle;
            item.ExecutionTime = now;
            item.LockHolder = null; item.LockedAt = null; item.LeaseUntil = null;
            item.ProgressPercent = null; item.ProgressMessage = null;
            _logger.LogWarning("Stale lease on {Id} ({Function}) held by {Owner}; restarting ({Count}/{Max}).", item.Id, item.Function, item.LockHolder, item.StaleRestartCount, max);
        }
        else
        {
            item.Status = TickerStatus.Cancelled;
            item.ExecutedAt = now;
            item.Exception = item.OnStale == StaleAction.Cancel ? "Cancelled: the owning node lost its lease (OnStale = Cancel)." : $"Cancelled after {item.StaleRestartCount} stale restart(s).";
            item.LockHolder = null; item.LeaseUntil = null;
            _logger.LogWarning("Stale lease on {Id} ({Function}); cancelling.", item.Id, item.Function);
        }
        var ok = await _provider.CompleteExecutionAsync(item, token, ct).ConfigureAwait(false);
        if (!ok) return;
        Interlocked.Increment(ref _state.TotalStaleRecovered);
        TempusMetrics.StaleRecovered.Add(1, new KeyValuePair<string, object?>("tempus.action", restart ? "restart" : "cancel"));
        foreach (var n in _services.GetServices<ITempusFailureNotifier>())
        {
            try { n.Notify(new TickerFailureEvent("stale_recovered", item.Function, item.Id, item.TickerType, restart ? "restarted" : "cancelled", item.RetryCount, item.Retries, now, _state.OwnerId)); } catch { }
        }
        if (!restart && item is TimeTickerEntity t) await _chains.OnTerminalAsync(t, ct).ConfigureAwait(false);
        (_services.GetService(typeof(TempusHostScheduler)) as TempusHostScheduler)?.Restart();
    }
}

/// <summary>Registers this node and reaps nodes without heartbeats.</summary>
public sealed class NodeHeartbeatService : BackgroundService
{
    private readonly HashSet<string> _warnedOwners = new();
    private readonly TempusDiagnostics _diagnostics;
    private readonly ITickerPersistenceProvider _provider;
    private readonly TempusOptions _options;
    private readonly TempusRuntimeState _state;
    private readonly TempusHostScheduler _scheduler;
    private readonly TickerFunctionRegistry _registry;
    private readonly ITickerClock _clock;
    private readonly ILogger<NodeHeartbeatService> _logger;

    public NodeHeartbeatService(ITickerPersistenceProvider provider, IOptions<TempusOptions> options, TempusRuntimeState state, TempusHostScheduler scheduler, TickerFunctionRegistry registry, ITickerClock clock, TempusDiagnostics diagnostics, ILogger<NodeHeartbeatService> logger)
    {
        _diagnostics = diagnostics;
        _provider = provider;
        _options = options.Value;
        _state = state;
        _scheduler = scheduler;
        _registry = registry;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = _clock.UtcNow;
                await _provider.UpsertNodeAsync(Snapshot(now), stoppingToken).ConfigureAwait(false);
                var mine = _registry.Functions.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var n in await _provider.ListNodesAsync(stoppingToken).ConfigureAwait(false))
                {
                    if (now - n.LastHeartbeatAt > _options.Scheduler.NodeOfflineAfter)
                    {
                        await _provider.RemoveNodeAsync(n.OwnerId, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                    if (n.OwnerId == _state.OwnerId || mine.SetEquals(n.Functions) || !_warnedOwners.Add(n.OwnerId)) continue;
                    var message = $"Node '{n.NodeIdentifier}' ({n.MachineName}) in namespace '{_options.Namespace}' registers different functions than this node. " +
                        "That is expected during a rolling deployment, but if it is a different application, give each application its own UseApplicationNamespace(...) value: " +
                        "nodes in one namespace pick up each other's jobs.";
                    _logger.LogWarning("{Message}", message);
                    _diagnostics.Warn(message);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Node heartbeat failed."); }
            try { await Task.Delay(_options.Scheduler.NodeHeartbeatInterval, stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
        try { await _provider.RemoveNodeAsync(_state.OwnerId, CancellationToken.None).ConfigureAwait(false); } catch { }
    }

    public TempusNodeEntity Snapshot(DateTime now)
    {
        var s = _options.Scheduler;
        return new TempusNodeEntity
        {
            OwnerId = _state.OwnerId,
            Namespace = _options.Namespace,
            NodeIdentifier = s.NodeIdentifier,
            DisplayName = s.NodeDisplayName,
            AdvertisedDashboardUrl = s.AdvertisedDashboardUrl,
            Version = typeof(NodeHeartbeatService).Assembly.GetName().Version?.ToString(),
            MachineName = Environment.MachineName,
            ProcessId = Environment.ProcessId,
            MaxConcurrency = s.MaxConcurrency,
            Running = _state.Running.Count,
            Queued = _state.QueuedCount,
            SchedulerRunning = _scheduler.IsRunning,
            Functions = _registry.Functions.Select(f => f.Name).ToList(),
            StartedAt = _state.StartedAt,
            LastHeartbeatAt = now,
        };
    }
}

/// <summary>Polls cancellation commands addressed to this node.</summary>
public sealed class ControlCommandService : BackgroundService
{
    private readonly ITickerPersistenceProvider _provider;
    private readonly TempusOptions _options;
    private readonly TempusRuntimeState _state;
    private readonly ITickerClock _clock;
    private readonly ILogger<ControlCommandService> _logger;

    public ControlCommandService(ITickerPersistenceProvider provider, IOptions<TempusOptions> options, TempusRuntimeState state, ITickerClock clock, ILogger<ControlCommandService> logger)
    {
        _provider = provider;
        _options = options.Value;
        _state = state;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(_options.Scheduler.ControlCommandPollInterval, stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            if (_state.Running.IsEmpty) continue;
            try
            {
                foreach (var cmd in await _provider.DequeueControlCommandsAsync(_state.OwnerId, _clock.UtcNow, stoppingToken).ConfigureAwait(false))
                {
                    if (cmd.Command == "cancel" && _state.TryCancelLocal(cmd.TickerId, cmd.AcquisitionToken))
                        _logger.LogInformation("Cancellation command applied to {Id}.", cmd.TickerId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Control command poll failed."); }
        }
    }
}

/// <summary>Deletes terminal work older than the configured retention windows.</summary>
public sealed class RetentionService : BackgroundService
{
    private readonly ITickerPersistenceProvider _provider;
    private readonly TempusOptions _options;
    private readonly ITickerClock _clock;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(ITickerPersistenceProvider provider, IOptions<TempusOptions> options, ITickerClock clock, ILogger<RetentionService> logger)
    {
        _provider = provider;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public DateTime? LastSweepAt { get; private set; }
    public int LastSweepDeleted { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var r = _options.Retention;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(r.Enabled ? r.SweepInterval : TimeSpan.FromMinutes(10), stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            try
            {
                var deleted = 0;
                if (r.Enabled) deleted = await SweepAsync(stoppingToken).ConfigureAwait(false);
                if (_options.ExecutionLogs.PersistToDatabase)
                    deleted += await _provider.DeleteExecutionLogsAsync(_clock.UtcNow - _options.ExecutionLogs.Retention, _options.ExecutionLogs.MaxStoredExecutions, stoppingToken).ConfigureAwait(false);
                LastSweepAt = _clock.UtcNow;
                LastSweepDeleted = deleted;
                if (deleted > 0) _logger.LogInformation("Retention sweep deleted {Count} item(s).", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Retention sweep failed."); }
        }
    }

    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var r = _options.Retention;
        var now = _clock.UtcNow;
        var windows = new List<RetentionWindow>();
        void Add(TimeSpan? span, params TickerStatus[] statuses) { if (span is not null) foreach (var s in statuses) windows.Add(new RetentionWindow(s, now - span.Value)); }
        Add(r.DeleteSucceededAfter, TickerStatus.Done, TickerStatus.DueDone);
        Add(r.DeleteFailedAfter, TickerStatus.Failed);
        Add(r.DeleteCancelledAfter, TickerStatus.Cancelled);
        Add(r.DeleteSkippedAfter, TickerStatus.Skipped);

        var deleted = 0;
        var batches = 0;
        foreach (var w in windows)
        {
            if (batches >= r.MaxBatchesPerSweep) break;
            var roots = await _provider.ListTerminalTimeTickerRootsAsync(w, r.BatchSize, ct).ConfigureAwait(false);
            batches++;
            var ids = new List<Guid>();
            foreach (var root in roots)
            {
                var chain = await _provider.GetChainAsync(root.Id, ct).ConfigureAwait(false);
                if (chain.Count > r.MaxNodesPerChain) continue;
                if (!chain.All(j => j.Status.IsTerminal() && Eligible(j, windows))) continue;
                ids.AddRange(chain.Select(j => j.Id));
            }
            if (ids.Count > 0) deleted += await _provider.DeleteTimeTickersAsync(ids, ct).ConfigureAwait(false);

            if (batches >= r.MaxBatchesPerSweep) break;
            var occ = await _provider.ListTerminalOccurrencesAsync(w, r.BatchSize, ct).ConfigureAwait(false);
            batches++;
            if (occ.Count > 0) deleted += await _provider.DeleteOccurrencesAsync(occ.Select(o => o.Id).ToList(), ct).ConfigureAwait(false);
        }
        return deleted;
    }

    private static bool Eligible(TimeTickerEntity j, List<RetentionWindow> windows)
    {
        var at = j.ExecutedAt ?? j.UpdatedAt;
        return windows.Any(w => w.Status == j.Status && at < w.Before);
    }
}

/// <summary>Applies declarative seeds (policies, crons, time tickers) and code seeders.</summary>
public sealed class TempusSeeder
{
    private readonly IServiceProvider _services;
    private readonly ITickerPersistenceProvider _provider;
    private readonly TempusOptions _options;
    private readonly TickerFunctionRegistry _registry;
    private readonly Policies.SchedulePolicyEvaluator _policies;
    private readonly ITickerClock _clock;
    private readonly ILogger<TempusSeeder> _logger;

    public TempusSeeder(IServiceProvider services, ITickerPersistenceProvider provider, IOptions<TempusOptions> options, TickerFunctionRegistry registry, Policies.SchedulePolicyEvaluator policies, ITickerClock clock, ILogger<TempusSeeder> logger)
    {
        _services = services;
        _provider = provider;
        _options = options.Value;
        _registry = registry;
        _policies = policies;
        _clock = clock;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct)
    {
        // policies first (crons may reference them)
        foreach (var p in _options.PolicySeeds.Policies)
        {
            var entity = p.Build();
            entity.Namespace = _options.Namespace;
            entity.IsSeeded = true;
            var existing = await _provider.GetSchedulePolicyAsync(entity.Name, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                entity.Id = existing.Id;
                entity.CreatedAt = existing.CreatedAt;
                entity.Revision = SameRules(existing, entity) ? existing.Revision : existing.Revision + 1;
            }
            await _policies.ValidateAsync(entity, ct).ConfigureAwait(false);
            entity.UpdatedAt = _clock.UtcNow;
            await _provider.UpsertSchedulePolicyAsync(entity, ct).ConfigureAwait(false);
        }

        if (!_options.IgnoreSeededCronTickers)
        {
            var seeds = _options.CronSeeds.Seeds.ToList();
            foreach (var f in _registry.Functions.Where(f => f.CronExpression is not null))
            {
                var fn = f;
                seeds.Add(($"attr:{fn.Name}", _ => Managers.CronSchedule.From(fn.CronExpression!).ToEntity(fn.Name)));
            }
            var existing = await _provider.ListCronTickersAsync(c => c.SeedKey != null, ct).ConfigureAwait(false);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, factory) in seeds)
            {
                if (!seen.Add(key)) throw new TempusException($"Cron seed key '{key}' is declared more than once.");
                var desired = factory(_services);
                desired.SeedKey = key;
                desired.Namespace = _options.Namespace;
                CronParser.Parse(desired.Expression);
                if (!_registry.Contains(desired.Function)) _logger.LogWarning("Seeded cron '{Key}' references function '{Function}' which is not registered on this host.", key, desired.Function);
                var current = existing.FirstOrDefault(c => c.SeedKey == key);
                if (current is null)
                {
                    desired.CreatedAt = desired.UpdatedAt = _clock.UtcNow;
                    await _provider.InsertCronTickerAsync(desired, ct).ConfigureAwait(false);
                    _logger.LogInformation("Seeded cron '{Key}' ({Function} @ {Expression}).", key, desired.Function, desired.Expression);
                }
                else
                {
                    var changed = current.Function != desired.Function || current.Expression != desired.Expression || current.TimeZoneId != desired.TimeZoneId
                        || current.SchedulePolicyName != desired.SchedulePolicyName || current.Request != desired.Request || current.ChainTemplateJson != desired.ChainTemplateJson
                        || current.SkipIfAlreadyRunning != desired.SkipIfAlreadyRunning || current.Retries != desired.Retries || !current.RetryIntervals.SequenceEqual(desired.RetryIntervals)
                        || current.TimeoutSeconds != desired.TimeoutSeconds || current.SkipAfterSeconds != desired.SkipAfterSeconds || current.CatchUpMode != desired.CatchUpMode
                        || current.Priority != desired.Priority || current.Description != desired.Description || current.Retired;
                    if (changed)
                    {
                        current.Function = desired.Function; current.Expression = desired.Expression; current.TimeZoneId = desired.TimeZoneId;
                        current.SchedulePolicyName = desired.SchedulePolicyName; current.Request = desired.Request; current.ChainTemplateJson = desired.ChainTemplateJson;
                        current.SkipIfAlreadyRunning = desired.SkipIfAlreadyRunning; current.Retries = desired.Retries; current.RetryIntervals = desired.RetryIntervals;
                        current.TimeoutSeconds = desired.TimeoutSeconds; current.SkipAfterSeconds = desired.SkipAfterSeconds; current.CatchUpMode = desired.CatchUpMode;
                        current.Priority = desired.Priority; current.Description = desired.Description; current.Retired = false;
                        current.DefinitionVersion++;
                        current.UpdatedAt = _clock.UtcNow;
                        await _provider.UpdateCronTickerAsync(current, ct).ConfigureAwait(false);
                        _logger.LogInformation("Updated seeded cron '{Key}'.", key);
                    }
                }
            }
            foreach (var orphan in existing.Where(c => !seen.Contains(c.SeedKey!) && !c.Retired))
            {
                orphan.Enabled = false;
                orphan.Retired = true;
                orphan.UpdatedAt = _clock.UtcNow;
                await _provider.UpdateCronTickerAsync(orphan, ct).ConfigureAwait(false);
                _logger.LogInformation("Retired seeded cron '{Key}' (no longer declared).", orphan.SeedKey);
            }
        }

        foreach (var (key, factory) in _options.TimeSeeds.Seeds)
        {
            var exists = await _provider.ListTimeTickersAsync(t => t.SeedKey == key, 1, ct).ConfigureAwait(false);
            if (exists.Count > 0) continue;
            var root = factory(_services);
            root.SeedKey = key;
            root.ExecutionTime ??= _clock.UtcNow;
            var all = Managers.TimeTickerManager.Flatten(root, _options.Namespace);
            await _provider.InsertTimeTickersAsync(all, ct).ConfigureAwait(false);
            _logger.LogInformation("Seeded time ticker '{Key}'.", key);
        }

        foreach (var seeder in _options.Seeders)
        {
            await using var scope = _services.CreateAsyncScope();
            await seeder(scope.ServiceProvider, ct).ConfigureAwait(false);
        }
    }

    private static bool SameRules(SchedulePolicyEntity a, SchedulePolicyEntity b) =>
        a.TimeZoneId == b.TimeZoneId && a.AllowedWeekdays == b.AllowedWeekdays && a.WindowStart == b.WindowStart && a.WindowEnd == b.WindowEnd
        && a.ExcludedDates.SequenceEqual(b.ExcludedDates) && a.ExcludedAnnualDates.SequenceEqual(b.ExcludedAnnualDates)
        && a.DateProviders.SequenceEqual(b.DateProviders) && a.Requires.SequenceEqual(b.Requires) && a.ExceptWhen.SequenceEqual(b.ExceptWhen) && a.Description == b.Description;
}
