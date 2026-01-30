namespace IamOrchestrator.Models;

public class Job
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Customer { get; set; } = string.Empty;
    public string ScriptPath { get; set; } = string.Empty;
    public string ContainerImage { get; set; } = string.Empty;
    public string? RegistryServer { get; set; }
    public string? RegistryUsername { get; set; }
    public string? RegistryPassword { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
    public JobType JobType { get; set; }
    public bool IsWhatIf { get; set; }
    public bool IsPaused { get; set; }
    public string? Schedule { get; set; }
    public JobStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public enum JobStatus
{
    Pending,
    Scheduled,  // Waiting for scheduled time
    Running,
    Completed,
    Failed
}

public enum JobType
{
    OneOff,
    Scheduled
}
