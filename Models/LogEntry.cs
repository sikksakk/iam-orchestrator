namespace IamOrchestrator.Models;

public sealed class LogEntry
{
    public Guid JobId { get; set; }
    public string Message { get; set; } = string.Empty;
    public LogLevel Level { get; set; }
    public string? Source { get; set; }
}

public enum LogLevel
{
    Info,
    Warning,
    Error,
    Debug
}
