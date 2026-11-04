namespace Tempus;

/// <summary>A class-based job. Register with <c>services.MapTicker&lt;T&gt;()</c>.</summary>
public interface ITickerFunction
{
    Task ExecuteAsync(TickerFunctionContext context, CancellationToken cancellationToken);
}

/// <summary>A class-based job with a typed request. Register with <c>services.MapTicker&lt;TFunction, TRequest&gt;()</c>.</summary>
public interface ITickerFunction<TRequest>
{
    Task ExecuteAsync(TickerFunctionContext<TRequest> context, CancellationToken cancellationToken);
}

/// <summary>A class-based job with a typed request that publishes a typed result.</summary>
public interface ITickerFunction<TRequest, TResult>
{
    Task<TResult> ExecuteAsync(TickerFunctionContext<TRequest> context, CancellationToken cancellationToken);
}

/// <summary>Called on each failed attempt and on observed cancellation. Registered with <c>options.SetExceptionHandler&lt;T&gt;()</c>. Singleton; keep it thread-safe.</summary>
public interface ITickerExceptionHandler
{
    Task HandleExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType);
    Task HandleCanceledExceptionAsync(Exception exception, Guid tickerId, TickerType tickerType);
}

/// <summary>Receives failure events (failed, timeout_cancelled, stale_recovered). Integrations build on this.</summary>
public interface ITempusFailureNotifier
{
    void Notify(TickerFailureEvent failureEvent);
}

public sealed record TickerFailureEvent(
    string Kind,
    string Function,
    Guid TickerId,
    TickerType TickerType,
    string? Reason,
    int RetryCount,
    int Retries,
    DateTime OccurredAtUtc,
    string Node);

/// <summary>Supplies excluded dates to schedule policies via <c>ExcludeDatesFrom(name)</c>. Must be fast, deterministic and local.</summary>
public interface IScheduleDateProvider
{
    string Name { get; }
    bool IsExcluded(DateOnly localDate);
    string GetReason(DateOnly localDate);
}

/// <summary>Start/stop control for the local scheduler loop.</summary>
public interface ITempusHostScheduler
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    /// <summary>Ask a running scheduler to recompute its next wake-up.</summary>
    void Restart();
}
