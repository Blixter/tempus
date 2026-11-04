namespace Tempus.Entities;

/// <summary>A command (currently only cancellation) written for the node that owns a running execution.</summary>
public class ControlCommandEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Namespace { get; set; } = string.Empty;
    public string TargetOwnerId { get; set; } = string.Empty;
    public string Command { get; set; } = "cancel";
    public TickerType TickerType { get; set; }
    public Guid TickerId { get; set; }
    public Guid? AcquisitionToken { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddSeconds(30);
}
