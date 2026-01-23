using IamOrchestrator.Models;

namespace IamOrchestrator.Services;

public interface IApiClient
{
    Task<List<Job>> GetPendingJobsAsync(string? customer = null);
    Task<Job?> GetJobAsync(Guid jobId);
    Task<bool> UpdateJobStatusAsync(Guid jobId, JobStatus status);
    Task<bool> SendLogAsync(LogEntry logEntry);
    Task SendHeartbeatAsync(Orchestrator orchestrator);
    Task<CertificateResponse?> GetCertificateAsync(string customerName);
}
