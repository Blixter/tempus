using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tempus.Entities;
using Tempus.Instrumentation;
using Tempus.Options;
using Tempus.Persistence;

namespace Tempus.Scheduling;

/// <summary>
/// The scheduler loop: plans cron occurrences, acquires due work, dispatches it through weighted priority queues
/// and runs it with bounded concurrency. One instance per process.
/// </summary>
public sealed class TempusHostScheduler : ITempusHostScheduler
{
    private readonly ITickerPersistenceProvider _provider;
    private readonly ExecutionEngine _engine;
    private readonly CronPlanner _planner;
    private readonly TempusOptions _options;
    private readonly ITickerClock _clock;
    private readonly TempusRuntimeState _state;
    private readonly TempusDiagnostics _diagnostics;
    private readonly ILogger<TempusHostScheduler> _logger;

    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly Channel<TickerExecutionEntity> _high = Channel.CreateUnbounded<TickerExecutionEntity>();
    private readonly Channel<TickerExecutionEntity> _normal = Channel.CreateUnbounded<TickerExecutionEntity>();
    private readonly Channel<TickerExecutionEntity> _low = Channel.CreateUnbounded<TickerExecutionEntity>();
    private readonly SemaphoreSlim _queueSignal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _capacity;
    private readonly HashSet<Guid> _inFlight = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private Task? _dispatchTask;
    private CancellationToken _hostStopping;
    private volatile bool _running;
    private int _cycle;

    public TempusHostScheduler(ITickerPersistenceProvider provider, ExecutionEngine engine, CronPlanner planner, IOptions<TempusOptions> options,
        ITickerClock clock, TempusRuntimeState state, TempusDiagnostics diagnostics, ILogger<TempusHostScheduler> logger)
    {
        _provider = provider;
        _engine = engine;
        _planner = planner;
        _options = options.Value;
        _clock = clock;
        _state = state;
        _diagnostics = diagnostics;
        _logger = logger;
        _capacity = new SemaphoreSlim(_options.Scheduler.MaxConcurrency, _options.Scheduler.MaxConcurrency);
    }

    public bool IsRunning => _running;
    public DateTime? NextWakeUp { get; private set; }
    public DateTime? LastAcquisitionAt { get; private set; }

    // ------------------------------------------------------------------ hosted service plumbing

    /// <summary>Called by the hosted service so in-flight work observes host shutdown.</summary>
    internal void AttachHost(CancellationToken hostStopping) => _hostStopping = hostStopping;

    public Task StartAsync(CancellationToken cancellationToken = default) => StartLoopAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) => StopLoopAsync(cancellationToken);

    private Task StartLoopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_running) return Task.CompletedTask;
            _running = true;
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_hostStopping);
            var token = _loopCts.Token;
            _loopTask = Task.Run(() => LoopAsync(token), CancellationToken.None);
            _dispatchTask = Task.Run(() => DispatchAsync(token), CancellationToken.None);
            _logger.LogInformation("Tempus scheduler started on {Owner} (namespace '{Namespace}', max concurrency {Max}).", _state.OwnerId, _options.Namespace, _options.Scheduler.MaxConcurrency);
        }
        return Task.CompletedTask;
    }

    private async Task StopLoopAsync(CancellationToken cancellationToken)
    {
        Task? loop, dispatch;
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            _loopCts?.Cancel();
            loop = _loopTask; dispatch = _dispatchTask;
        }
        try { if (loop is not null) await loop.ConfigureAwait(false); } catch { }
        try { if (dispatch is not null) await dispatch.ConfigureAwait(false); } catch { }
        // Drain in-flight work (bounded).
        var deadline = Task.Delay(_options.Scheduler.ShutdownDrainTimeout, CancellationToken.None);
        while (_state.Running.Count > 0 && !deadline.IsCompleted)
            await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Tempus scheduler stopped on {Owner}.", _state.OwnerId);
    }

    public void Restart()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    // ------------------------------------------------------------------ acquisition loop

    private async Task LoopAsync(CancellationToken ct)
    {
        var s = _options.Scheduler;
        var firstRun = true;
        var replan = false;
        while (!ct.IsCancellationRequested)
        {
            DateTime? next = null;
            try
            {
                var now = _clock.UtcNow;
                var nextCron = await _planner.PlanAsync(now, firstRun, ct, replan).ConfigureAwait(false);
                firstRun = false;
                replan = await AcquireAsync(now, ct).ConfigureAwait(false);
                var nextTime = await _provider.GetNextTimeTickerExecutionTimeAsync(now, ct).ConfigureAwait(false);
                var nextOcc = await _provider.GetNextOccurrenceExecutionTimeAsync(now, ct).ConfigureAwait(false);
                next = Min(nextTime, nextOcc, nextCron);
                _diagnostics.LastFallbackSuccess = _clock.UtcNow;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _diagnostics.FallbackFailures);
                TempusMetrics.FallbackFailures.Add(1);
                _diagnostics.StoreError(ex);
                _logger.LogError(ex, "Scheduler loop iteration failed; retrying after {Delay}.", s.FallbackIntervalChecker);
            }

            var wait = s.FallbackIntervalChecker;
            if (next is not null)
            {
                var until = next.Value - _clock.UtcNow;
                if (until < wait) wait = until;
            }
            if (wait < s.MinPollingInterval) wait = s.MinPollingInterval;
            if (wait > s.FallbackIntervalChecker) wait = s.FallbackIntervalChecker;
            NextWakeUp = _clock.UtcNow + wait;
            try { await _wake.WaitAsync(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            // collapse extra wake signals
            while (_wake.CurrentCount > 0) _wake.Wait(0);
        }
    }

    private static DateTime? Min(params DateTime?[] values)
    {
        DateTime? min = null;
        foreach (var v in values) if (v is not null && (min is null || v < min)) min = v;
        return min;
    }

    private async Task<bool> AcquireAsync(DateTime now, CancellationToken ct)
    {
        var s = _options.Scheduler;
        var room = s.MaxQueuedExecutions - _state.QueuedCount;
        if (room <= 0) return false;
        var batch = Math.Min(s.AcquisitionBatchSize, room);
        var leaseUntil = now + s.LeaseDuration;

        var time = await _provider.AcquireDueTimeTickersAsync(_state.OwnerId, now, leaseUntil, batch, ct).ConfigureAwait(false);
        foreach (var t in time) Enqueue(t);

        var acquiredOccurrences = false;
        room = s.MaxQueuedExecutions - _state.QueuedCount;
        if (room > 0)
        {
            var occ = await _provider.AcquireDueOccurrencesAsync(_state.OwnerId, now, leaseUntil, Math.Min(batch, room), ct).ConfigureAwait(false);
            foreach (var o in occ) Enqueue(o);
            acquiredOccurrences = occ.Count > 0;
        }
        if (time.Count > 0 || acquiredOccurrences) LastAcquisitionAt = now;
        return acquiredOccurrences;
    }

    private void Enqueue(TickerExecutionEntity item)
    {
        lock (_inFlight)
        {
            if (!_inFlight.Add(item.Id)) return;
        }
        _state.IncrementQueued();
        _state.Running[item.Id] = new RunningExecution { Id = item.Id, Type = item.TickerType, Function = item.Function, Token = item.AcquisitionToken ?? Guid.Empty, Cancellation = new CancellationTokenSource() };
        TempusMetrics.Enqueued.Add(1, TempusMetrics.FunctionTag(item.Function));
        var channel = item.Priority switch
        {
            TickerTaskPriority.High => _high,
            TickerTaskPriority.Low => _low,
            _ => _normal,
        };
        channel.Writer.TryWrite(item);
        _queueSignal.Release();
    }

    // ------------------------------------------------------------------ dispatch

    private async Task DispatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(ct).ConfigureAwait(false);
                var item = Dequeue();
                if (item is null) continue;
                await _capacity.WaitAsync(ct).ConfigureAwait(false);
                _state.DecrementQueued();
                var shutdown = _hostStopping;
                if (item.Priority == TickerTaskPriority.LongRunning)
                {
                    _ = Task.Factory.StartNew(() => RunAndRelease(item, shutdown), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
                }
                else
                {
                    _ = Task.Run(() => RunAndRelease(item, shutdown), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Dispatch loop error."); }
        }
    }

    private async Task RunAndRelease(TickerExecutionEntity item, CancellationToken shutdown)
    {
        try
        {
            if (_state.Running.TryGetValue(item.Id, out var placeholder)) placeholder.Cancellation.Dispose();
            await _engine.ExecuteAsync(item, shutdown).ConfigureAwait(false);
        }
        finally
        {
            lock (_inFlight) _inFlight.Remove(item.Id);
            _capacity.Release();
        }
    }

    /// <summary>Weighted 4:2:1 selection: High, Normal, Low.</summary>
    private TickerExecutionEntity? Dequeue()
    {
        var slot = Interlocked.Increment(ref _cycle) % 7;
        Channel<TickerExecutionEntity>[] order = slot switch
        {
            0 or 1 or 2 or 3 => [_high, _normal, _low],
            4 or 5 => [_normal, _high, _low],
            _ => [_low, _high, _normal],
        };
        foreach (var c in order)
            if (c.Reader.TryRead(out var item)) return item;
        return null;
    }

    public (int High, int Normal, int Low) QueueDepths() => (_high.Reader.Count, _normal.Reader.Count, _low.Reader.Count);
}

/// <summary>Starts the scheduler according to the configured start mode and stops it on host shutdown.</summary>
public sealed class TempusSchedulerHostedService : IHostedService
{
    private readonly TempusHostScheduler _scheduler;
    private readonly TempusOptions _options;
    private readonly IHostApplicationLifetime _lifetime;

    public TempusSchedulerHostedService(TempusHostScheduler scheduler, IOptions<TempusOptions> options, IHostApplicationLifetime lifetime)
    {
        _scheduler = scheduler;
        _options = options.Value;
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _scheduler.AttachHost(_lifetime.ApplicationStopping);
        return _options.StartMode == TempusStartMode.Immediate ? _scheduler.StartAsync(cancellationToken) : Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _scheduler.StopAsync(cancellationToken);
}
