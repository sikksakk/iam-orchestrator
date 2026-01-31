using IamOrchestrator.Models;

namespace IamOrchestrator.Services;

public interface IApiClient
{
    Task<List<Job>> GetPendingJobsAsync(string orchestratorId, string customer, CancellationToken cancellationToken = default);
    Task<Job?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<bool> UpdateJobStatusAsync(Guid jobId, JobStatus status, CancellationToken cancellationToken = default);
    Task<bool> SendLogAsync(LogEntry logEntry, CancellationToken cancellationToken = default);
    Task SendHeartbeatAsync(Orchestrator orchestrator, CancellationToken cancellationToken = default);
    Task<CertificateResponse?> GetCertificateAsync(string customerName, CancellationToken cancellationToken = default);
    Task<bool> CheckForUpdateAsync(string orchestratorId, CancellationToken cancellationToken = default);
    Task AcknowledgeUpdateAsync(string orchestratorId, CancellationToken cancellationToken = default);
    Task<Job?> RefreshCredentialsAsync(Guid jobId, CancellationToken cancellationToken = default);
}
