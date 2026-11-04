using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tempus.Entities;
using Tempus.Options;
using Tempus.Persistence;

namespace Tempus.Logging;

/// <summary>Bounded in-memory buffer for one execution's log tail.</summary>
public sealed class ExecutionLogBuffer
{
    private readonly object _gate = new();
    private readonly LinkedList<ExecutionLogLine> _lines = new();
    private readonly int _maxLines;
    private readonly int _maxBytes;
    private int _bytes;

    public ExecutionLogBuffer(Guid tickerId, TickerType type, string function, int maxLines, int maxBytes)
    {
        TickerId = tickerId;
        Type = type;
        Function = function;
        _maxLines = Math.Max(1, maxLines);
        _maxBytes = Math.Max(256, maxBytes);
    }

    public Guid TickerId { get; }
    public TickerType Type { get; }
    public string Function { get; }
    public bool Truncated { get; private set; }
    public bool Dirty { get; private set; }

    public void Add(ExecutionLogLine line)
    {
        lock (_gate)
        {
            var size = Encoding.UTF8.GetByteCount(line.Message) + (line.Exception is null ? 0 : Encoding.UTF8.GetByteCount(line.Exception)) + 64;
            _lines.AddLast(line);
            _bytes += size;
            while (_lines.Count > _maxLines || (_bytes > _maxBytes && _lines.Count > 1))
            {
                var first = _lines.First!.Value;
                _bytes -= Encoding.UTF8.GetByteCount(first.Message) + (first.Exception is null ? 0 : Encoding.UTF8.GetByteCount(first.Exception)) + 64;
                _lines.RemoveFirst();
                Truncated = true;
            }
            Dirty = true;
        }
    }

    public IReadOnlyList<ExecutionLogLine> Snapshot()
    {
        lock (_gate) { Dirty = false; return _lines.ToList(); }
    }

    /// <summary>Copy of the lines without clearing the dirty flag (for readers).</summary>
    public IReadOnlyList<ExecutionLogLine> Peek()
    {
        lock (_gate) return _lines.ToList();
    }

    public int Count { get { lock (_gate) return _lines.Count; } }
    public int Bytes { get { lock (_gate) return _bytes; } }

    public static byte[] Compress(IReadOnlyList<ExecutionLogLine> lines)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(lines, TickerHelper.JsonOptions);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(json);
        return ms.ToArray();
    }

    public static IReadOnlyList<ExecutionLogLine> Decompress(byte[] payload)
    {
        if (payload.Length == 0) return [];
        using var ms = new MemoryStream(payload);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<List<ExecutionLogLine>>(gz, TickerHelper.JsonOptions) ?? [];
    }
}

/// <summary>Flows the current execution's buffer through async calls.</summary>
public static class ExecutionLogScope
{
    private static readonly AsyncLocal<ExecutionLogBuffer?> Current = new();
    public static ExecutionLogBuffer? Buffer { get => Current.Value; set => Current.Value = value; }
}

/// <summary>Captures ILogger messages written inside a job's execution scope.</summary>
public sealed class TempusLoggerProvider : ILoggerProvider
{
    private readonly ExecutionLogOptions _options;
    public TempusLoggerProvider(IOptions<TempusOptions> options) => _options = options.Value.ExecutionLogs;
    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, _options);
    public void Dispose() { }

    private sealed class CaptureLogger(string category, ExecutionLogOptions options) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => options.Enabled && logLevel >= options.MinimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var buffer = ExecutionLogScope.Buffer;
            if (buffer is null) return;
            if (category.StartsWith("Tempus.", StringComparison.Ordinal)) return;
            foreach (var prefix in options.ExcludedCategoryPrefixes)
                if (category.StartsWith(prefix, StringComparison.Ordinal)) return;
            var message = formatter(state, exception);
            if (options.Redact is not null) message = options.Redact(message);
            buffer.Add(new ExecutionLogLine(DateTime.UtcNow, logLevel.ToString(), category, message, exception?.ToString(), false));
        }
    }
}

/// <summary>Holds live buffers for executions on this host and flushes them to the store.</summary>
public sealed class ExecutionLogStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ExecutionLogBuffer> _live = new();
    // Recently finished tails kept on this node so the dashboard can show them even without database persistence.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ExecutionLogBuffer> _recent = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> _recentOrder = new();
    private const int MaxRecent = 1000;
    private readonly ITickerPersistenceProvider _provider;
    private readonly ExecutionLogOptions _options;
    private readonly ILogger<ExecutionLogStore> _logger;
    private long _pendingWrites;
    private long _writeFailures;
    private long _dropped;
    private DateTime? _lastFlush;

    public ExecutionLogStore(ITickerPersistenceProvider provider, IOptions<TempusOptions> options, ILogger<ExecutionLogStore> logger)
    {
        _provider = provider;
        _options = options.Value.ExecutionLogs;
        _logger = logger;
    }

    public long PendingWrites => Interlocked.Read(ref _pendingWrites);
    public long WriteFailures => Interlocked.Read(ref _writeFailures);
    public long DroppedUnflushed => Interlocked.Read(ref _dropped);
    public DateTime? LastSuccessfulFlush => _lastFlush;
    public bool PersistEnabled => _options.PersistToDatabase;

    public ExecutionLogBuffer Begin(Guid id, TickerType type, string function)
    {
        var buffer = new ExecutionLogBuffer(id, type, function, _options.MaxLinesPerExecution, _options.MaxBytesPerExecution);
        _live[id] = buffer;
        return buffer;
    }

    public void Lifecycle(ExecutionLogBuffer? buffer, string message)
    {
        if (buffer is null || !_options.IncludeLifecycleEvents) return;
        buffer.Add(new ExecutionLogLine(DateTime.UtcNow, "Information", "Tempus.Lifecycle", message, null, true));
    }

    public async Task EndAsync(ExecutionLogBuffer buffer, CancellationToken ct)
    {
        _live.TryRemove(buffer.TickerId, out _);
        if (_recent.TryAdd(buffer.TickerId, buffer)) _recentOrder.Enqueue(buffer.TickerId);
        else _recent[buffer.TickerId] = buffer;
        while (_recentOrder.Count > MaxRecent && _recentOrder.TryDequeue(out var old)) _recent.TryRemove(old, out _);
        await FlushAsync(buffer, ct).ConfigureAwait(false);
    }

    public async Task FlushAsync(ExecutionLogBuffer buffer, CancellationToken ct)
    {
        if (!_options.PersistToDatabase || !_options.Enabled) return;
        var lines = buffer.Snapshot();
        if (lines.Count == 0) return;
        Interlocked.Increment(ref _pendingWrites);
        try
        {
            await _provider.UpsertExecutionLogAsync(new ExecutionLogEntity
            {
                TickerId = buffer.TickerId,
                Namespace = _provider.Namespace,
                TickerType = buffer.Type,
                Function = buffer.Function,
                LineCount = lines.Count,
                ByteCount = buffer.Bytes,
                Truncated = buffer.Truncated,
                Payload = ExecutionLogBuffer.Compress(lines),
                UpdatedAt = DateTime.UtcNow,
            }, ct).ConfigureAwait(false);
            _lastFlush = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref _writeFailures);
            Interlocked.Increment(ref _dropped);
            _logger.LogWarning(ex, "Failed to persist execution log tail for {TickerId}.", buffer.TickerId);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingWrites);
        }
    }

    /// <summary>Live buffer (running on this host) or the persisted tail.</summary>
    public async Task<(IReadOnlyList<ExecutionLogLine> Lines, bool Live, bool Truncated)> GetTailAsync(Guid id, CancellationToken ct)
    {
        if (_live.TryGetValue(id, out var live)) return (live.Peek(), true, live.Truncated);
        var stored = await _provider.GetExecutionLogAsync(id, ct).ConfigureAwait(false);
        if (stored is null && _recent.TryGetValue(id, out var recent)) return (recent.Peek(), false, recent.Truncated);
        return stored is null ? ([], false, false) : (ExecutionLogBuffer.Decompress(stored.Payload), false, stored.Truncated);
    }

    public async Task FlushAllAsync(CancellationToken ct)
    {
        foreach (var b in _live.Values.Where(b => b.Dirty).ToList())
            await FlushAsync(b, ct).ConfigureAwait(false);
    }
}
