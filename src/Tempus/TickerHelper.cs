using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tempus;

public static class TickerHelper
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serialize a request payload for <c>TimeTickerEntity.Request</c> / <c>CronTickerEntity.Request</c>.</summary>
    public static string CreateTickerRequest<T>(T request) => JsonSerializer.Serialize(request, JsonOptions);

    public static T? ReadTickerRequest<T>(string? json) => json is null ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);

    public static object? ReadTickerRequest(string? json, Type type) => json is null ? null : JsonSerializer.Deserialize(json, type, JsonOptions);

    public static string Serialize(object? value, Type? type = null) => type is null ? JsonSerializer.Serialize(value, JsonOptions) : JsonSerializer.Serialize(value, type, JsonOptions);
}

/// <summary>Helpers that build <c>RetryIntervals</c> arrays (seconds). Set <c>Retries</c> separately.</summary>
public static class RetryPolicy
{
    /// <summary>Same delay for every retry. <c>Fixed(30, 3)</c> = [30, 30, 30].</summary>
    public static int[] Fixed(int intervalSeconds, int retries)
    {
        if (retries < 0) throw new ArgumentOutOfRangeException(nameof(retries));
        var result = new int[retries];
        Array.Fill(result, Math.Max(0, intervalSeconds));
        return result;
    }

    /// <summary>Doubles the delay each retry, capped at <paramref name="maxSeconds"/>. <c>Exponential(5, 4)</c> = [5, 10, 20, 40].</summary>
    public static int[] Exponential(int baseSeconds, int retries, int maxSeconds = 3600, bool jitter = false)
    {
        if (retries < 0) throw new ArgumentOutOfRangeException(nameof(retries));
        var result = new int[retries];
        var rng = jitter ? Random.Shared : null;
        for (var i = 0; i < retries; i++)
        {
            var raw = Math.Min((long)baseSeconds << Math.Min(i, 30), maxSeconds);
            if (rng is not null)
            {
                var factor = 1 + (rng.NextDouble() * 0.4 - 0.2);
                raw = (long)Math.Round(raw * factor);
            }
            result[i] = (int)Math.Max(0, raw);
        }
        return result;
    }

    internal static TimeSpan GetDelay(int[] intervals, int retryNumber)
    {
        if (intervals is null || intervals.Length == 0) return TimeSpan.FromSeconds(30);
        var idx = Math.Min(Math.Max(retryNumber - 1, 0), intervals.Length - 1);
        return TimeSpan.FromSeconds(Math.Max(0, intervals[idx]));
    }
}

/// <summary>The clock used by managers and the scheduler. Replace it in tests.</summary>
public interface ITickerClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemTickerClock : ITickerClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Result of a manager operation. <see cref="IsSucceeded"/> means the provider accepted the operation, not that a job ran.</summary>
public sealed class TickerResult<T>
{
    public bool IsSucceeded { get; init; }
    public Exception? Exception { get; init; }
    public T? Result { get; init; }
    public int AffectedRows { get; init; }

    public static TickerResult<T> Success(T? result, int affectedRows = 1) => new() { IsSucceeded = true, Result = result, AffectedRows = affectedRows };
    public static TickerResult<T> Failure(Exception exception) => new() { IsSucceeded = false, Exception = exception };
    public static TickerResult<T> Failure(string message) => new() { IsSucceeded = false, Exception = new TempusException(message) };

    /// <summary>Rethrows the failure with its original stack trace, or returns this result.</summary>
    public TickerResult<T> EnsureSuccess()
    {
        if (IsSucceeded) return this;
        if (Exception is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(Exception).Throw();
        throw new TempusException("Operation failed.");
    }
}

public class TempusException : Exception
{
    public TempusException(string message) : base(message) { }
    public TempusException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Terminal outcome of an execution as observed by <c>WaitForCompletionAsync</c> / <c>GetOutcomeAsync</c>.</summary>
public sealed class ExecutionOutcome
{
    public Guid Id { get; init; }
    public TickerType Type { get; init; }
    public TickerStatus Status { get; init; }
    public bool IsTerminal => Status.IsTerminal();
    public bool IsSucceeded => Status.IsSuccess();
    public bool HasResult { get; init; }
    public string? ResultJson { get; init; }
    public string? Exception { get; init; }
    public string? SkipReason { get; init; }
    public DateTime? ExecutedAt { get; init; }
    public long? ElapsedMilliseconds { get; init; }
    public int RetryCount { get; init; }

    public T? GetResult<T>() => HasResult ? TickerHelper.ReadTickerRequest<T>(ResultJson) : throw new TempusException("The execution did not publish a result.");
}
