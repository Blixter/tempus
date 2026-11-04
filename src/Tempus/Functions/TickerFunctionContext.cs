using System.Text.Json;

namespace Tempus;

/// <summary>Execution context passed to a job.</summary>
public class TickerFunctionContext
{
    private readonly Func<int, string?, CancellationToken, Task> _reportProgress;
    private readonly Action _requestCancellation;

    internal TickerFunctionContext(
        Guid id,
        string functionName,
        TickerType type,
        DateTime? scheduledFor,
        int retryCount,
        Guid? parentId,
        Guid? chainRootId,
        bool isDue,
        bool isTestRun,
        string? requestJson,
        string? parentResultJson,
        bool hasParentResult,
        Func<int, string?, CancellationToken, Task> reportProgress,
        Action requestCancellation,
        IServiceProvider services,
        string? description)
    {
        Id = id;
        FunctionName = functionName;
        Type = type;
        ScheduledFor = scheduledFor;
        RetryCount = retryCount;
        ParentId = parentId;
        ChainRootId = chainRootId;
        IsDue = isDue;
        IsTestRun = isTestRun;
        RequestJson = requestJson;
        ParentResultJson = parentResultJson;
        HasParentResult = hasParentResult;
        _reportProgress = reportProgress;
        _requestCancellation = requestCancellation;
        Services = services;
        Description = description;
    }

    /// <summary>Time ticker id or cron occurrence id.</summary>
    public Guid Id { get; }
    public string FunctionName { get; }
    public string? Description { get; }
    public TickerType Type { get; }
    /// <summary>UTC time the execution was scheduled for.</summary>
    public DateTime? ScheduledFor { get; }
    /// <summary>0 on the first attempt.</summary>
    public int RetryCount { get; }
    public Guid? ParentId { get; }
    public Guid? ChainRootId { get; }
    /// <summary>True when the execution started later than the scheduler's due threshold (overdue path).</summary>
    public bool IsDue { get; }
    /// <summary>True for dashboard "test once" runs that create no durable records.</summary>
    public bool IsTestRun { get; }
    /// <summary>Raw JSON request payload.</summary>
    public string? RequestJson { get; }
    /// <summary>The scoped service provider for this attempt.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Whether the direct parent published a result.</summary>
    public bool HasParentResult { get; }
    public string? ParentResultJson { get; }

    /// <summary>Result set by the function (serialized on success).</summary>
    public string? ResultJson { get; private set; }
    public bool ResultSet { get; private set; }

    /// <summary>Report durable progress (0..100, message capped at 500 chars). Do not call in a tight loop: each call is a provider write.</summary>
    public Task ReportProgressAsync(int percent, string? message = null, CancellationToken cancellationToken = default)
    {
        if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent), "Percent must be between 0 and 100.");
        if (message is { Length: > 500 }) message = message[..500];
        return _reportProgress(percent, message, cancellationToken);
    }

    /// <summary>Publish a result. Only persisted after a successful terminal outcome. Repeated calls keep the last value.</summary>
    public void SetResult<T>(T value)
    {
        ResultJson = TickerHelper.Serialize(value, typeof(T));
        ResultSet = true;
    }

    internal void SetResultObject(object? value, Type? type)
    {
        ResultJson = TickerHelper.Serialize(value, type);
        ResultSet = true;
    }

    /// <summary>Deserialize the direct parent's committed result. Throws when none exists.</summary>
    public T? GetParentResult<T>()
    {
        if (!HasParentResult) throw new TempusException("The parent did not publish a result.");
        return TickerHelper.ReadTickerRequest<T>(ParentResultJson);
    }

    /// <summary>Deserialize the request payload as <typeparamref name="T"/>.</summary>
    public T? GetRequest<T>() => TickerHelper.ReadTickerRequest<T>(RequestJson);

    /// <summary>Cooperatively cancel this execution's own token.</summary>
    public void RequestCancellation() => _requestCancellation();
}

/// <summary>Execution context with a typed request.</summary>
public sealed class TickerFunctionContext<TRequest> : TickerFunctionContext
{
    private readonly Lazy<TRequest?> _request;

    internal TickerFunctionContext(TickerFunctionContext inner) : base(
        inner.Id, inner.FunctionName, inner.Type, inner.ScheduledFor, inner.RetryCount, inner.ParentId, inner.ChainRootId,
        inner.IsDue, inner.IsTestRun, inner.RequestJson, inner.ParentResultJson, inner.HasParentResult,
        (p, m, ct) => inner.ReportProgressAsync(p, m, ct), inner.RequestCancellation, inner.Services, inner.Description)
    {
        Inner = inner;
        _request = new Lazy<TRequest?>(() => TickerHelper.ReadTickerRequest<TRequest>(inner.RequestJson));
    }

    internal TickerFunctionContext Inner { get; }

    /// <summary>The deserialized request payload.</summary>
    public TRequest Request => _request.Value ?? throw new TempusException($"Job {Id} has no request payload of type {typeof(TRequest).Name}.");

    public new void SetResult<T>(T value)
    {
        base.SetResult(value);
        Inner.SetResult(value);
    }
}
