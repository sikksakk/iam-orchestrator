using IamOrchestrator.Models;

namespace IamOrchestrator.Services;

public interface IApiClient
{
    Task<List<Job>> GetPendingJobsAsync(string orchestratorId, string customer);
    Task<Job?> GetJobAsync(Guid jobId);
    Task<bool> UpdateJobStatusAsync(Guid jobId, JobStatus status);
    Task<bool> SendLogAsync(LogEntry logEntry);
    Task SendHeartbeatAsync(Orchestrator orchestrator);
    Task<CertificateResponse?> GetCertificateAsync(string customerName);
    Task<bool> CheckForUpdateAsync(string orchestratorId);
    Task AcknowledgeUpdateAsync(string orchestratorId);
    Task<Job?> RefreshCredentialsAsync(Guid jobId);
}
