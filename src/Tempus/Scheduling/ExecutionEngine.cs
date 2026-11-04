using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tempus.Entities;
using Tempus.Functions;
using Tempus.Instrumentation;
using Tempus.Logging;
using Tempus.Options;
using Tempus.Persistence;

namespace Tempus.Scheduling;

/// <summary>Runs one acquired execution through the full pipeline: lateness check, overlap check, concurrency permit, attempt, outcome.</summary>
public sealed class ExecutionEngine
{
    private readonly IServiceProvider _services;
    private readonly ITickerPersistenceProvider _provider;
    private readonly TickerFunctionRegistry _registry;
    private readonly TempusOptions _options;
    private readonly ITickerClock _clock;
    private readonly TempusRuntimeState _state;
    private readonly ChainCoordinator _chains;
    private readonly ExecutionLogStore _logs;
    private readonly ILogger<ExecutionEngine> _logger;
    private readonly TempusDiagnostics _diagnostics;

    public ExecutionEngine(IServiceProvider services, ITickerPersistenceProvider provider, TickerFunctionRegistry registry, IOptions<TempusOptions> options,
        ITickerClock clock, TempusRuntimeState state, ChainCoordinator chains, ExecutionLogStore logs, ILogger<ExecutionEngine> logger, TempusDiagnostics diagnostics)
    {
        _services = services;
        _provider = provider;
        _registry = registry;
        _options = options.Value;
        _clock = clock;
        _state = state;
        _chains = chains;
        _logs = logs;
        _logger = logger;
        _diagnostics = diagnostics;
    }

    /// <summary>Execute an acquired item. The item must carry a valid acquisition token.</summary>
    public async Task ExecuteAsync(TickerExecutionEntity item, CancellationToken shutdown)
    {
        var token = item.AcquisitionToken ?? throw new InvalidOperationException("Execution without acquisition token.");
        var scheduler = _options.Scheduler;
        var now = _clock.UtcNow;
        var buffer = _logs.Begin(item.Id, item.TickerType, item.Function);
        ExecutionLogScope.Buffer = buffer;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var run = new RunningExecution { Id = item.Id, Type = item.TickerType, Function = item.Function, Token = token, Cancellation = cts };
        _state.Running[item.Id] = run;
        TempusMetrics.Active.Add(1, TempusMetrics.FunctionTag(item.Function));
        _logs.Lifecycle(buffer, $"Queued on {_state.OwnerId} (attempt {item.RetryCount + 1}).");

        try
        {
            // ---- lateness
            if (item.SkipAfterSeconds is { } grace && item.ExecutionTime is { } scheduled && now - scheduled > TimeSpan.FromSeconds(grace))
            {
                await SkipAsync(item, token, $"Started {(now - scheduled).TotalSeconds:0}s after the scheduled time; grace window is {grace}s.", shutdown).ConfigureAwait(false);
                return;
            }

            // ---- cron overlap and definition drift
            if (item is CronTickerOccurrenceEntity occ)
            {
                var cron = await _provider.GetCronTickerAsync(occ.CronTickerId, shutdown).ConfigureAwait(false);
                if (cron is null)
                {
                    await SkipAsync(item, token, "The cron definition no longer exists.", shutdown).ConfigureAwait(false);
                    return;
                }
                if (!occ.IsManual && occ.DefinitionVersion != cron.DefinitionVersion)
                {
                    await SkipAsync(item, token, "The cron definition changed after this occurrence was planned.", shutdown).ConfigureAwait(false);
                    return;
                }
                if (cron.SkipIfAlreadyRunning && await _provider.HasActiveOccurrenceAsync(cron.Id, occ.Id, shutdown).ConfigureAwait(false))
                {
                    await SkipAsync(item, token, "Skipped because a previous run of this cron is still unfinished (SkipIfAlreadyRunning).", shutdown).ConfigureAwait(false);
                    return;
                }
                if (cron.ChainTemplateJson is not null && cron.Chain is { Children.Count: > 0 } template)
                {
                    await LaunchCronChainAsync(cron, occ, template, token, shutdown).ConfigureAwait(false);
                    return;
                }
                if (cron.Request is not null && item.Request is null) item.Request = cron.Request;
            }

            if (!_registry.TryGet(item.Function, out var descriptor))
            {
                await FailTerminalAsync(item, token, new TempusException($"Function '{item.Function}' is not registered on this host."), shutdown).ConfigureAwait(false);
                return;
            }

            // ---- per-function concurrency permit
            var permit = descriptor.Semaphore;
            if (permit is not null)
            {
                _logs.Lifecycle(buffer, $"Waiting for a concurrency permit ({descriptor.MaxConcurrency}).");
                await permit.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            try
            {
                await RunAttemptAsync(item, descriptor, token, run, buffer, cts, shutdown).ConfigureAwait(false);
            }
            finally
            {
                permit?.Release();
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            _logger.LogInformation("Execution {Id} interrupted by shutdown; it will be recovered by the stale watchdog.", item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled engine error for {Id}.", item.Id);
            _diagnostics.StoreError(ex);
        }
        finally
        {
            _state.Running.TryRemove(item.Id, out _);
            TempusMetrics.Active.Add(-1, TempusMetrics.FunctionTag(item.Function));
            ExecutionLogScope.Buffer = null;
            try { await _logs.EndAsync(buffer, CancellationToken.None).ConfigureAwait(false); } catch { }
            cts.Dispose();
        }
    }

    private async Task LaunchCronChainAsync(CronTickerEntity cron, CronTickerOccurrenceEntity occ, CronChainTemplate template, Guid token, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (occ.ChainRootId is not null)
        {
            // Already launched (recovered dispatch): nothing more to do.
            occ.Status = TickerStatus.Done;
            occ.ExecutedAt = now;
            await _provider.CompleteExecutionAsync(occ, token, ct).ConfigureAwait(false);
            return;
        }
        var root = await _chains.MaterializeCronChainAsync(cron, occ, template, ct).ConfigureAwait(false);
        occ.ChainRootId = root.Id;
        occ.Status = TickerStatus.Done;
        occ.StartedAt = now;
        occ.ExecutedAt = now;
        occ.ElapsedMilliseconds = 0;
        await _provider.CompleteExecutionAsync(occ, token, ct).ConfigureAwait(false);
        await UpdateCronLastRunAsync(occ, TickerStatus.Done, ct).ConfigureAwait(false);
        _logger.LogInformation("Cron {Cron} occurrence {Occurrence} launched chain {Root}.", cron.Function, occ.Id, root.Id);
    }

    private async Task RunAttemptAsync(TickerExecutionEntity item, TickerFunctionDescriptor descriptor, Guid token, RunningExecution run, ExecutionLogBuffer buffer, CancellationTokenSource cts, CancellationToken shutdown)
    {
        var scheduler = _options.Scheduler;
        var now = _clock.UtcNow;
        var isDue = item.ExecutionTime is { } sched && now - sched > scheduler.DueThreshold;

        // ---- mark InProgress
        item.Status = TickerStatus.InProgress;
        item.StartedAt = now;
        item.ProgressPercent = null;
        item.ProgressMessage = null;
        item.ProgressUpdatedAt = null;
        if (!await _provider.CompleteExecutionAsync(item, token, shutdown).ConfigureAwait(false))
        {
            _logger.LogWarning("Lost ownership of {Id} before it started.", item.Id);
            return;
        }
        _logs.Lifecycle(buffer, $"Running {item.Function} (attempt {item.RetryCount + 1}/{item.Retries + 1}).");
        if (item is TimeTickerEntity tt) await _chains.OnStartedAsync(tt, shutdown).ConfigureAwait(false);
        if (item.CancellationRequested) cts.Cancel();

        // ---- parent result
        string? parentResult = null;
        var hasParentResult = false;
        Guid? parentId = null, chainRootId = null;
        if (item is TimeTickerEntity t && t.ParentId is not null)
        {
            parentId = t.ParentId;
            chainRootId = t.ChainRootId;
            var parent = await _provider.GetTimeTickerAsync(t.ParentId.Value, shutdown).ConfigureAwait(false);
            if (parent is { HasResult: true }) { parentResult = parent.Result; hasParentResult = true; }
        }

        // ---- timeout
        var timeout = item.TimeoutSeconds switch
        {
            null => scheduler.DefaultExecutionTimeout,
            <= 0 => null,
            var s => TimeSpan.FromSeconds(s.Value),
        };
        using var timeoutCts = new CancellationTokenSource();
        var timedOut = false;
        if (timeout is not null) timeoutCts.CancelAfter(timeout.Value);
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token);
        using var reg = timeoutCts.Token.Register(() => { timedOut = true; });

        var sw = Stopwatch.StartNew();
        using var activity = _options.ActivitySourceEnabled
            ? TempusActivitySource.Source.StartActivity(item.TickerType == TickerType.TimeTicker ? "tempus.job.execute.timeticker" : "tempus.job.execute.crontickeroccurrence")
            : null;
        activity?.SetTag("tempus.job.id", item.Id);
        activity?.SetTag("tempus.function", item.Function);
        activity?.SetTag("tempus.priority", item.Priority.ToString());
        activity?.SetTag("tempus.retry_count", item.RetryCount);
        if (parentId is not null) activity?.SetTag("tempus.parent_id", parentId);

        TickerFunctionContext? context = null;
        Exception? failure = null;
        var cancelled = false;
        (bool HasResult, object? Result, Type? ResultType) outcome = default;

        await using var scope = _services.CreateAsyncScope();
        try
        {
            context = new TickerFunctionContext(item.Id, item.Function, item.TickerType, item.ExecutionTime, item.RetryCount, parentId, chainRootId, isDue, false,
                item.Request, parentResult, hasParentResult,
                async (p, m, ct2) =>
                {
                    await _provider.UpdateProgressAsync(item.TickerType, item.Id, token, p, m, _clock.UtcNow, ct2).ConfigureAwait(false);
                    _logs.Lifecycle(buffer, $"Progress {p}%{(m is null ? "" : ": " + m)}");
                },
                () => cts.Cancel(), scope.ServiceProvider, item.Description);

            outcome = await descriptor.Invoker(scope.ServiceProvider, context, attemptCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce) when (attemptCts.IsCancellationRequested || shutdown.IsCancellationRequested)
        {
            cancelled = true;
            failure = oce;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        sw.Stop();

        if (shutdown.IsCancellationRequested && cancelled && !timedOut && !cts.IsCancellationRequested)
        {
            // Interrupted by host shutdown: leave the lease to expire so another node (or this one after restart) recovers it.
            _logs.Lifecycle(buffer, "Interrupted by host shutdown.");
            return;
        }

        var finishedAt = _clock.UtcNow;
        item.ElapsedMilliseconds = sw.ElapsedMilliseconds;
        TempusMetrics.Duration.Record(sw.Elapsed.TotalMilliseconds, TempusMetrics.FunctionTag(item.Function), TempusMetrics.SuccessTag(failure is null));

        if (failure is null)
        {
            item.Status = isDue ? TickerStatus.DueDone : TickerStatus.Done;
            item.ExecutedAt = finishedAt;
            item.Exception = null;
            item.ExceptionType = null;
            if (outcome.HasResult && outcome.ResultType is not null)
            {
                item.Result = TickerHelper.Serialize(outcome.Result, outcome.ResultType);
                item.HasResult = true;
            }
            else if (context!.ResultSet)
            {
                item.Result = context.ResultJson;
                item.HasResult = true;
            }
            item.LockHolder = null; item.LeaseUntil = null;
            var ok = await _provider.CompleteExecutionAsync(item, token, shutdown).ConfigureAwait(false);
            if (!ok) { _logger.LogWarning("Completion of {Id} rejected: ownership changed.", item.Id); return; }
            Interlocked.Increment(ref _state.TotalCompleted);
            TempusMetrics.Completed.Add(1, TempusMetrics.FunctionTag(item.Function));
            activity?.SetTag("tempus.outcome", item.Status.ToString());
            _logs.Lifecycle(buffer, $"Completed as {item.Status} in {sw.ElapsedMilliseconds} ms.");
            _logger.LogInformation("{Type} {Id} ({Function}) completed as {Status} in {Elapsed} ms.", item.TickerType, item.Id, item.Function, item.Status, sw.ElapsedMilliseconds);
            await AfterTerminalAsync(item, shutdown).ConfigureAwait(false);
            return;
        }

        var handler = scope.ServiceProvider.GetService<ITickerExceptionHandler>();

        if (cancelled)
        {
            item.Status = TickerStatus.Cancelled;
            item.ExecutedAt = finishedAt;
            item.Exception = timedOut ? $"Timed out after {timeout}." : "Cancelled.";
            item.ExceptionType = failure.GetType().FullName;
            item.LockHolder = null; item.LeaseUntil = null;
            await _provider.CompleteExecutionAsync(item, token, shutdown).ConfigureAwait(false);
            Interlocked.Increment(ref _state.TotalCancelled);
            TempusMetrics.Cancelled.Add(1, TempusMetrics.FunctionTag(item.Function));
            activity?.SetTag("tempus.outcome", "Cancelled");
            _logs.Lifecycle(buffer, timedOut ? $"Cancelled by timeout after {sw.ElapsedMilliseconds} ms." : "Cancelled.");
            _logger.LogWarning("{Type} {Id} ({Function}) was cancelled{Timeout}.", item.TickerType, item.Id, item.Function, timedOut ? " by timeout" : "");
            if (handler is not null) await SafeHandlerAsync(() => handler.HandleCanceledExceptionAsync(failure, item.Id, item.TickerType)).ConfigureAwait(false);
            Notify(timedOut ? "timeout_cancelled" : "cancelled", item, item.Exception);
            await AfterTerminalAsync(item, shutdown).ConfigureAwait(false);
            return;
        }

        // ---- failure: retry or fail
        if (handler is not null) await SafeHandlerAsync(() => handler.HandleExceptionAsync(failure, item.Id, item.TickerType)).ConfigureAwait(false);
        activity?.SetTag("tempus.exception_type", failure.GetType().FullName);
        item.Exception = failure.ToString();
        item.ExceptionType = failure.GetType().FullName;

        if (item.RetryCount < item.Retries)
        {
            item.RetryCount++;
            var delay = RetryPolicy.GetDelay(item.RetryIntervals, item.RetryCount);
            item.Status = TickerStatus.Idle;
            item.ExecutionTime = finishedAt.Add(delay);
            item.LockHolder = null; item.LockedAt = null; item.LeaseUntil = null;
            await _provider.CompleteExecutionAsync(item, token, shutdown).ConfigureAwait(false);
            Interlocked.Increment(ref _state.TotalRetried);
            TempusMetrics.Retried.Add(1, TempusMetrics.FunctionTag(item.Function));
            _logs.Lifecycle(buffer, $"Attempt failed: {failure.GetType().Name}: {failure.Message}. Retry {item.RetryCount}/{item.Retries} in {delay.TotalSeconds:0}s.");
            _logger.LogWarning(failure, "{Type} {Id} ({Function}) failed; retry {Retry}/{Retries} scheduled in {Delay}.", item.TickerType, item.Id, item.Function, item.RetryCount, item.Retries, delay);
            await _logs.FlushAsync(buffer, shutdown).ConfigureAwait(false);
            (_services.GetService(typeof(TempusHostScheduler)) as TempusHostScheduler)?.Restart();
            return;
        }

        item.Status = TickerStatus.Failed;
        item.ExecutedAt = finishedAt;
        item.LockHolder = null; item.LeaseUntil = null;
        await _provider.CompleteExecutionAsync(item, token, shutdown).ConfigureAwait(false);
        Interlocked.Increment(ref _state.TotalFailed);
        TempusMetrics.Failed.Add(1, TempusMetrics.FunctionTag(item.Function));
        activity?.SetTag("tempus.outcome", "Failed");
        _logs.Lifecycle(buffer, $"Failed: {failure.GetType().Name}: {failure.Message}");
        _logger.LogError(failure, "{Type} {Id} ({Function}) failed after {Attempts} attempt(s).", item.TickerType, item.Id, item.Function, item.RetryCount + 1);
        Notify("failed", item, failure.Message);
        await AfterTerminalAsync(item, shutdown).ConfigureAwait(false);
    }

    private async Task FailTerminalAsync(TickerExecutionEntity item, Guid token, Exception ex, CancellationToken ct)
    {
        item.Status = TickerStatus.Failed;
        item.ExecutedAt = _clock.UtcNow;
        item.Exception = ex.ToString();
        item.ExceptionType = ex.GetType().FullName;
        item.LockHolder = null; item.LeaseUntil = null;
        await _provider.CompleteExecutionAsync(item, token, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _state.TotalFailed);
        TempusMetrics.Failed.Add(1, TempusMetrics.FunctionTag(item.Function));
        _logger.LogError(ex, "{Type} {Id} ({Function}) failed.", item.TickerType, item.Id, item.Function);
        Notify("failed", item, ex.Message);
        await AfterTerminalAsync(item, ct).ConfigureAwait(false);
    }

    private async Task SkipAsync(TickerExecutionEntity item, Guid token, string reason, CancellationToken ct)
    {
        item.Status = TickerStatus.Skipped;
        item.SkipReason = reason;
        item.ExecutedAt = _clock.UtcNow;
        item.LockHolder = null; item.LeaseUntil = null;
        await _provider.CompleteExecutionAsync(item, token, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _state.TotalSkipped);
        TempusMetrics.Skipped.Add(1, TempusMetrics.FunctionTag(item.Function));
        _logs.Lifecycle(ExecutionLogScope.Buffer, $"Skipped: {reason}");
        _logger.LogInformation("{Type} {Id} ({Function}) skipped: {Reason}", item.TickerType, item.Id, item.Function, reason);
        await AfterTerminalAsync(item, ct).ConfigureAwait(false);
    }

    private async Task AfterTerminalAsync(TickerExecutionEntity item, CancellationToken ct)
    {
        try
        {
            if (item is TimeTickerEntity t) await _chains.OnTerminalAsync(t, ct).ConfigureAwait(false);
            else if (item is CronTickerOccurrenceEntity o) await UpdateCronLastRunAsync(o, item.Status, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Post-completion processing failed for {Id}.", item.Id);
            _diagnostics.StoreError(ex);
        }
        (_services.GetService(typeof(TempusHostScheduler)) as TempusHostScheduler)?.Restart();
    }

    private async Task UpdateCronLastRunAsync(CronTickerOccurrenceEntity occ, TickerStatus status, CancellationToken ct)
    {
        var cron = await _provider.GetCronTickerAsync(occ.CronTickerId, ct).ConfigureAwait(false);
        if (cron is null) return;
        cron.LastRunAt = occ.ExecutedAt ?? _clock.UtcNow;
        cron.LastStatus = status;
        await _provider.UpdateCronTickerAsync(cron, ct).ConfigureAwait(false);
    }

    private void Notify(string kind, TickerExecutionEntity item, string? reason)
    {
        foreach (var n in _services.GetServices<ITempusFailureNotifier>())
        {
            try { n.Notify(new TickerFailureEvent(kind, item.Function, item.Id, item.TickerType, reason, item.RetryCount, item.Retries, _clock.UtcNow, _state.OwnerId)); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failure notifier threw."); }
        }
    }

    private async Task SafeHandlerAsync(Func<Task> call)
    {
        try { await call().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "ITickerExceptionHandler threw."); }
    }

    // ------------------------------------------------------------------ test runs (dashboard "Test once")

    /// <summary>Run a function once through the normal pipeline without creating durable records.</summary>
    public async Task<TestRunResult> TestRunAsync(string function, string? requestJson, TimeSpan timeout, CancellationToken ct)
    {
        if (!_registry.TryGet(function, out var descriptor)) return new TestRunResult(false, null, $"Function '{function}' is not registered.", [], 0);
        timeout = TimeSpan.FromSeconds(Math.Clamp(timeout.TotalSeconds, 1, 300));
        var id = Guid.NewGuid();
        var buffer = new ExecutionLogBuffer(id, TickerType.TimeTicker, function, _options.ExecutionLogs.MaxLinesPerExecution, _options.ExecutionLogs.MaxBytesPerExecution);
        ExecutionLogScope.Buffer = buffer;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        var permit = descriptor.Semaphore;
        if (permit is not null) await permit.WaitAsync(cts.Token).ConfigureAwait(false);
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var context = new TickerFunctionContext(id, function, TickerType.TimeTicker, _clock.UtcNow, 0, null, null, false, true, requestJson, null, false,
                (p, m, _) => { buffer.Add(new ExecutionLogLine(DateTime.UtcNow, "Information", "Tempus.Lifecycle", $"Progress {p}%{(m is null ? "" : ": " + m)}", null, true)); return Task.CompletedTask; },
                () => cts.Cancel(), scope.ServiceProvider, "Test run");
            var outcome = await descriptor.Invoker(scope.ServiceProvider, context, cts.Token).ConfigureAwait(false);
            var result = outcome.HasResult && outcome.ResultType is not null ? TickerHelper.Serialize(outcome.Result, outcome.ResultType) : context.ResultJson;
            return new TestRunResult(true, result, null, buffer.Snapshot(), sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new TestRunResult(false, null, ex.ToString(), buffer.Snapshot(), sw.ElapsedMilliseconds);
        }
        finally
        {
            permit?.Release();
            ExecutionLogScope.Buffer = null;
        }
    }
}

public sealed record TestRunResult(bool Succeeded, string? ResultJson, string? Error, IReadOnlyList<ExecutionLogLine> Logs, long ElapsedMilliseconds);
