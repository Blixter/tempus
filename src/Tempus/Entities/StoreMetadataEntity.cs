namespace Tempus.Entities;

/// <summary>Key/value metadata per namespace (seed bookkeeping, schema info).</summary>
public class StoreMetadataEntity
{
    public string Namespace { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
