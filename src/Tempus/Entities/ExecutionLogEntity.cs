namespace Tempus.Entities;

/// <summary>Compressed log tail of one execution (one row per ticker id).</summary>
public class ExecutionLogEntity
{
    public Guid TickerId { get; set; }
    public string Namespace { get; set; } = string.Empty;
    public TickerType TickerType { get; set; }
    public string Function { get; set; } = string.Empty;
    public int LineCount { get; set; }
    public int ByteCount { get; set; }
    public bool Truncated { get; set; }
    /// <summary>GZip-compressed JSON array of <see cref="ExecutionLogLine"/>.</summary>
    public byte[] Payload { get; set; } = [];
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed record ExecutionLogLine(DateTime Timestamp, string Level, string Category, string Message, string? Exception, bool Lifecycle);
