using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Tempus.Instrumentation;

/// <summary>Activity source and meter. Subscribe with <c>tracing.AddSource(TempusActivitySource.Name)</c> and <c>metrics.AddMeter(TempusMetrics.MeterName)</c>.</summary>
public static class TempusActivitySource
{
    public const string Name = "Tempus";
    public static readonly ActivitySource Source = new(Name, typeof(TempusActivitySource).Assembly.GetName().Version?.ToString());
}

public static class TempusMetrics
{
    public const string MeterName = "Tempus";
    public static readonly Meter Meter = new(MeterName, typeof(TempusMetrics).Assembly.GetName().Version?.ToString());

    public static readonly Counter<long> Enqueued = Meter.CreateCounter<long>("tempus.jobs.enqueued");
    public static readonly Counter<long> Completed = Meter.CreateCounter<long>("tempus.jobs.completed");
    public static readonly Counter<long> Failed = Meter.CreateCounter<long>("tempus.jobs.failed");
    public static readonly Counter<long> Retried = Meter.CreateCounter<long>("tempus.jobs.retried");
    public static readonly Counter<long> Cancelled = Meter.CreateCounter<long>("tempus.jobs.cancelled");
    public static readonly Counter<long> Skipped = Meter.CreateCounter<long>("tempus.jobs.skipped");
    public static readonly UpDownCounter<long> Active = Meter.CreateUpDownCounter<long>("tempus.jobs.active");
    public static readonly Counter<long> StaleRecovered = Meter.CreateCounter<long>("tempus.jobs.stale_recovered");
    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("tempus.job.duration", unit: "ms");
    public static readonly Counter<long> FallbackFailures = Meter.CreateCounter<long>("tempus.fallback.failures");

    public static KeyValuePair<string, object?> FunctionTag(string function) => new("tempus.function", function);
    public static KeyValuePair<string, object?> SuccessTag(bool success) => new("tempus.success", success);
}
