using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Tempus.Options;

namespace Tempus.Scheduling;

/// <summary>A locally running (or queued) execution.</summary>
public sealed class RunningExecution
{
    public required Guid Id { get; init; }
    public required TickerType Type { get; init; }
    public required string Function { get; init; }
    public required Guid Token { get; init; }
    public required CancellationTokenSource Cancellation { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public volatile bool TimeoutPending;
}

/// <summary>Per-process runtime state shared by the scheduler, managers and dashboard.</summary>
public sealed class TempusRuntimeState
{
    public TempusRuntimeState(IOptions<TempusOptions> options)
    {
        var s = options.Value.Scheduler;
        OwnerId = $"{s.NodeIdentifier}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        StartedAt = DateTime.UtcNow;
    }

    /// <summary>Unique per process instance.</summary>
    public string OwnerId { get; }
    public DateTime StartedAt { get; }
    public ConcurrentDictionary<Guid, RunningExecution> Running { get; } = new();
    public int QueuedCount => _queued;
    public int RunningCount => Running.Count(r => !r.Value.TimeoutPending || true);
    private int _queued;

    public long TotalCompleted; public long TotalFailed; public long TotalCancelled; public long TotalSkipped; public long TotalRetried; public long TotalStaleRecovered;

    internal void IncrementQueued() => Interlocked.Increment(ref _queued);
    internal void DecrementQueued() => Interlocked.Decrement(ref _queued);

    /// <summary>Signal a local execution to cancel. Returns true when it was found.</summary>
    public bool TryCancelLocal(Guid id, Guid? token = null)
    {
        if (!Running.TryGetValue(id, out var run)) return false;
        if (token is not null && run.Token != token) return false;
        try { run.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }
}

/// <summary>Node-local diagnostics exposed by the dashboard.</summary>
public sealed class TempusDiagnostics
{
    public ConcurrentQueue<(DateTime At, string Message)> Warnings { get; } = new();
    public DateTime? LastFallbackSuccess { get; set; }
    public long FallbackFailures;
    public DateTime? LastStoreError { get; set; }
    public string? LastStoreErrorMessage { get; set; }
    public long StoreErrors;

    public void Warn(string message)
    {
        Warnings.Enqueue((DateTime.UtcNow, message));
        while (Warnings.Count > 50) Warnings.TryDequeue(out _);
    }

    public void StoreError(Exception ex)
    {
        Interlocked.Increment(ref StoreErrors);
        LastStoreError = DateTime.UtcNow;
        LastStoreErrorMessage = ex.Message;
    }
}
