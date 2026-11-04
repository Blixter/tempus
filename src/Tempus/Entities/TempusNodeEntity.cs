namespace Tempus.Entities;

/// <summary>A scheduler replica registered through heartbeats.</summary>
public class TempusNodeEntity
{
    public string OwnerId { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string NodeIdentifier { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? AdvertisedDashboardUrl { get; set; }
    public string? Version { get; set; }
    public string? MachineName { get; set; }
    public int ProcessId { get; set; }
    public int MaxConcurrency { get; set; }
    public int Running { get; set; }
    public int Queued { get; set; }
    public bool SchedulerRunning { get; set; }
    public List<string> Functions { get; set; } = [];
    public DateTime StartedAt { get; set; }
    public DateTime LastHeartbeatAt { get; set; }
}
