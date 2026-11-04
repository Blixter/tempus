namespace Tempus;

/// <summary>
/// Marks a method as a job. Discovered by reflection at startup (entry assembly + <c>AddDiscovery(...)</c>).
/// The method may take <see cref="TickerFunctionContext"/>, <see cref="TickerFunctionContext{TRequest}"/>, <see cref="CancellationToken"/>
/// and <see cref="IServiceProvider"/> in any order, and return void, Task, ValueTask, T, Task&lt;T&gt; or ValueTask&lt;T&gt;.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class TickerFunctionAttribute : Attribute
{
    public TickerFunctionAttribute(string functionName)
    {
        FunctionName = functionName;
    }

    /// <summary>Unique, stable function name. Persisted jobs refer to it.</summary>
    public string FunctionName { get; }

    /// <summary>Optional cron expression. When set, a code-owned cron ticker is seeded with key <c>attr:{FunctionName}</c>.</summary>
    public string? CronExpression { get; set; }

    /// <summary>Per-host concurrency limit for this function (0 = unlimited).</summary>
    public int MaxConcurrency { get; set; }

    public TickerTaskPriority Priority { get; set; } = TickerTaskPriority.Normal;

    /// <summary>Declared result type. Not required: the generator infers it from the return type when omitted.</summary>
    public Type? ResultType { get; set; }
}

/// <summary>Selects the constructor used to instantiate a job class that has several.</summary>
[AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false)]
public sealed class TempusConstructorAttribute : Attribute { }
