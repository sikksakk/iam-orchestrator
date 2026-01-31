namespace IamOrchestrator.Models;

public class Orchestrator
{
    public string Id { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public DateTime LastHeartbeat { get; set; }
    public string Version { get; set; } = "1.0.0";
    public string HostName { get; set; } = string.Empty;
    public bool PendingUpdate { get; set; } = false;
}
