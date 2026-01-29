using IamOrchestrator.Models;
using IamOrchestrator.Services;

namespace IamOrchestrator;

public class OrchestratorWorker : BackgroundService
{
    private readonly ILogger<OrchestratorWorker> _logger;
    private readonly IApiClient _apiClient;
    private readonly IContainerExecutor _containerExecutor;
    private readonly IJobScheduler _jobScheduler;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly HashSet<Guid> _processedOneOffJobs = new();
    private readonly string? _customerName;
    private readonly string _orchestratorId;
    private readonly string _hostName;

    public OrchestratorWorker(
        ILogger<OrchestratorWorker> logger,
        IApiClient apiClient,
        IContainerExecutor containerExecutor,
        IJobScheduler jobScheduler,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _apiClient = apiClient;
        _containerExecutor = containerExecutor;
        _jobScheduler = jobScheduler;
        _configuration = configuration;
        _lifetime = lifetime;
        _customerName = configuration["CustomerSettings:CustomerName"];
        _hostName = Environment.MachineName;
        _orchestratorId = $"{_hostName}-{Guid.NewGuid().ToString()[..8]}";
        
        if (string.IsNullOrEmpty(_customerName))
        {
            _logger.LogWarning("No customer name configured. This orchestrator will process jobs for ALL customers.");
        }
        else
        {
            _logger.LogInformation("Orchestrator {OrchestratorId} configured for customer: {CustomerName}", _orchestratorId, _customerName);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Orchestrator Worker started at: {time}", DateTimeOffset.Now);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Send heartbeat
                await SendHeartbeatAsync();
                
                // Check for update request
                if (await CheckForUpdateAsync())
                {
                    _logger.LogInformation("Update requested - orchestrator will exit to allow restart");
                    // Request graceful application shutdown - container platform will restart us
                    _lifetime.StopApplication();
                    return;
                }
                
                // Process jobs
                await ProcessJobsAsync(stoppingToken);

                var pollingInterval = _configuration.GetValue<int>("ApiSettings:PollingIntervalSeconds", 10);
                await Task.Delay(TimeSpan.FromSeconds(pollingInterval), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in orchestrator main loop");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("Orchestrator Worker stopping...");
    }

    private async Task<bool> CheckForUpdateAsync()
    {
        try
        {
            _logger.LogDebug("Checking for updates for orchestrator {OrchestratorId}...", _orchestratorId);
            var pendingUpdate = await _apiClient.CheckForUpdateAsync(_orchestratorId);
            _logger.LogDebug("Update check result for {OrchestratorId}: pendingUpdate={PendingUpdate}", _orchestratorId, pendingUpdate);
            
            if (pendingUpdate)
            {
                _logger.LogInformation("Pending update detected for orchestrator {OrchestratorId}", _orchestratorId);
                await _apiClient.AcknowledgeUpdateAsync(_orchestratorId);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for updates for orchestrator {OrchestratorId}", _orchestratorId);
            return false;
        }
    }

    private async Task SendHeartbeatAsync()
    {
        try
        {
            var orchestrator = new Orchestrator
            {
                Id = _orchestratorId,
                CustomerName = _customerName ?? string.Empty,
                HostName = _hostName,
                Version = "1.0.0",
                LastHeartbeat = DateTime.UtcNow
            };

            await _apiClient.SendHeartbeatAsync(orchestrator);
            _logger.LogDebug("Heartbeat sent for orchestrator {OrchestratorId}", _orchestratorId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send heartbeat");
        }
    }

    private async Task ProcessJobsAsync(CancellationToken stoppingToken)
    {
        try
        {
            var pendingJobs = await _apiClient.GetPendingJobsAsync(_customerName);
            
            if (pendingJobs.Any())
            {
                var customerInfo = string.IsNullOrEmpty(_customerName) ? "all customers" : $"customer '{_customerName}'";
                _logger.LogInformation("Found {Count} pending jobs for {Customer}", pendingJobs.Count, customerInfo);
            }

            foreach (var job in pendingJobs)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                await ProcessJobAsync(job, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing jobs");
        }
    }

    private async Task ProcessJobAsync(Job job, CancellationToken stoppingToken)
    {
        try
        {
            if (job.JobType == JobType.Scheduled)
            {
                // Handle scheduled jobs
                if (!_jobScheduler.IsJobScheduled(job.Id))
                {
                    _logger.LogInformation("Scheduling job {JobId} ({JobName}) with schedule: {Schedule}", 
                        job.Id, job.Name, job.Schedule);
                    
                    _jobScheduler.ScheduleJob(job, async () => await ExecuteJobAsync(job));
                    
                    await SendLogAsync(job.Id, $"Job scheduled with cron expression: {job.Schedule}", Models.LogLevel.Info);
                }
                else if (job.IsPaused)
                {
                    _logger.LogDebug("Job {JobId} is paused", job.Id);
                }
            }
            else // OneOff
            {
                // Execute one-off jobs immediately (only once)
                if (!_processedOneOffJobs.Contains(job.Id))
                {
                    _processedOneOffJobs.Add(job.Id);
                    _logger.LogInformation("Executing one-off job {JobId} ({JobName})", job.Id, job.Name);
                    
                    // Execute in background so we don't block polling
                    _ = Task.Run(async () => await ExecuteJobAsync(job), stoppingToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job {JobId}", job.Id);
            await SendLogAsync(job.Id, $"Error processing job: {ex.Message}", Models.LogLevel.Error);
        }
    }

    private async Task ExecuteJobAsync(Job job)
    {
        try
        {
            _logger.LogDebug("Starting execution of job {JobId} ({JobName})", job.Id, job.Name);
            
            // Update status to Running
            await _apiClient.UpdateJobStatusAsync(job.Id, JobStatus.Running);
            await SendLogAsync(job.Id, $"Job execution started", Models.LogLevel.Info);

            // Download certificate for customer if customer is specified
            byte[]? certificatePfx = null;
            string? certificatePassword = null;
            
            if (!string.IsNullOrEmpty(job.Customer))
            {
                _logger.LogInformation("Downloading certificate for customer: {Customer}", job.Customer);
                var certificate = await _apiClient.GetCertificateAsync(job.Customer);
                
                if (certificate != null)
                {
                    certificatePfx = Convert.FromBase64String(certificate.CertificateData);
                    certificatePassword = certificate.Password;
                    
                    await SendLogAsync(job.Id, 
                        $"Certificate acquired for {job.Customer} (expires: {certificate.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC)", 
                        Models.LogLevel.Info);
                    
                    _logger.LogInformation("Certificate downloaded - Thumbprint: {Thumbprint}, Expires: {ExpiresAt}", 
                        certificate.Thumbprint, certificate.ExpiresAt);
                }
                else
                {
                    await SendLogAsync(job.Id, 
                        $"Warning: Could not obtain certificate for {job.Customer}", 
                        Models.LogLevel.Warning);
                }
            }

            // Execute the container with certificate
            var success = await _containerExecutor.ExecuteJobAsync(
                job, 
                async (logMessage) => await SendLogAsync(job.Id, logMessage, Models.LogLevel.Info, "orchestrator"),
                async (logMessage) => await SendLogAsync(job.Id, logMessage, Models.LogLevel.Info, "container"),
                certificatePfx,
                certificatePassword);

            // Update final status
            var finalStatus = success ? JobStatus.Completed : JobStatus.Failed;
            await _apiClient.UpdateJobStatusAsync(job.Id, finalStatus);
            
            var statusMessage = success ? "Job completed successfully" : "Job failed";
            await SendLogAsync(job.Id, statusMessage, success ? Models.LogLevel.Info : Models.LogLevel.Error);
            
            if (success)
            {
                _logger.LogInformation("✓ Job {JobId} ({JobName}) completed successfully for customer {Customer}", 
                    job.Id, job.Name, job.Customer);
            }
            else
            {
                _logger.LogError("✗ Job {JobId} ({JobName}) failed for customer {Customer}", 
                    job.Id, job.Name, job.Customer);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing job {JobId}", job.Id);
            await _apiClient.UpdateJobStatusAsync(job.Id, JobStatus.Failed);
            await SendLogAsync(job.Id, $"Job execution failed: {ex.Message}", Models.LogLevel.Error);
        }
    }

    private async Task SendLogAsync(Guid jobId, string message, Models.LogLevel level, string? source = null)
    {
        try
        {
            var logEntry = new LogEntry
            {
                JobId = jobId,
                Message = message,
                Level = level,
                Source = source ?? "orchestrator"
            };

            await _apiClient.SendLogAsync(logEntry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send log to API for job {JobId}", jobId);
        }
    }

    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Orchestrator Worker is stopping...");
        await base.StopAsync(stoppingToken);
    }
}
