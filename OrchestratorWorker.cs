using System.Collections.Concurrent;
using IamOrchestrator.Models;
using IamOrchestrator.Services;

namespace IamOrchestrator;

public sealed class OrchestratorWorker : BackgroundService
{
    private readonly ILogger<OrchestratorWorker> _logger;
    private readonly IApiClient _apiClient;
    private readonly IContainerExecutor _containerExecutor;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    
    // Thread-safe dictionary tracking processed jobs with timestamp for cleanup
    private readonly ConcurrentDictionary<Guid, DateTime> _processedOneOffJobs = new();
    private const int MaxProcessedJobsToKeep = 1000;
    
    private readonly string? _customerName;
    private readonly string _orchestratorId;
    private readonly string _hostName;

    public OrchestratorWorker(
        ILogger<OrchestratorWorker> logger,
        IApiClient apiClient,
        IContainerExecutor containerExecutor,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _apiClient = apiClient;
        _containerExecutor = containerExecutor;
        _configuration = configuration;
        _lifetime = lifetime;
        _customerName = configuration["CustomerSettings:CustomerName"];
        _hostName = Environment.MachineName;
        _orchestratorId = $"{_hostName}-{Guid.NewGuid().ToString()[..8]}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Validate customer name at startup with graceful error handling
        if (string.IsNullOrEmpty(_customerName))
        {
            _logger.LogCritical("");
            _logger.LogCritical("╔══════════════════════════════════════════════════════════════════╗");
            _logger.LogCritical("║  ❌ CONFIGURATION ERROR                                          ║");
            _logger.LogCritical("╠══════════════════════════════════════════════════════════════════╣");
            _logger.LogCritical("║  CustomerSettings:CustomerName is required.                      ║");
            _logger.LogCritical("║  Each orchestrator must handle a specific customer.              ║");
            _logger.LogCritical("║                                                                  ║");
            _logger.LogCritical("║  Please set the customer name in one of the following ways:      ║");
            _logger.LogCritical("║                                                                  ║");
            _logger.LogCritical("║  1. In appsettings.json:                                         ║");
            _logger.LogCritical("║     \"CustomerSettings\": {{ \"CustomerName\": \"your-customer\" }}     ║");
            _logger.LogCritical("║                                                                  ║");
            _logger.LogCritical("║  2. Via environment variable:                                    ║");
            _logger.LogCritical("║     CustomerSettings__CustomerName=your-customer                 ║");
            _logger.LogCritical("║                                                                  ║");
            _logger.LogCritical("║  3. Via command line:                                            ║");
            _logger.LogCritical("║     dotnet run -- --CustomerSettings:CustomerName=your-customer  ║");
            _logger.LogCritical("╚══════════════════════════════════════════════════════════════════╝");
            _logger.LogCritical("");
            
            // Stop the application gracefully
            _lifetime.StopApplication();
            return;
        }
        
        _logger.LogInformation("Orchestrator {OrchestratorId} configured for customer: {CustomerName}", _orchestratorId, _customerName);
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
            // Customer name is guaranteed to be set (validated in constructor)
            var pendingJobs = await _apiClient.GetPendingJobsAsync(_orchestratorId, _customerName!);
            
            if (pendingJobs.Any())
            {
                _logger.LogInformation("Received {Count} job(s) for orchestrator {OrchestratorId} (customer: {Customer})",
                    pendingJobs.Count, _orchestratorId, _customerName);
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
            // Log a nice banner with job details
            LogJobReceivedBanner(job);
            
            // All jobs are treated as one-off executions
            // The API is responsible for scheduling and resetting jobs to Pending when they should run
            if (_processedOneOffJobs.TryAdd(job.Id, DateTime.UtcNow))
            {
                _logger.LogInformation("Executing job {JobId} ({JobName})", job.Id, job.Name);
                
                // Cleanup old entries to prevent unbounded growth
                CleanupProcessedJobsCache();
                
                // Execute in background so we don't block polling
                // Wrap in try-catch to prevent silent exception swallowing
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteJobAsync(job);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Background execution failed for job {JobId}", job.Id);
                    }
                }, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job {JobId}", job.Id);
            await SendLogAsync(job.Id, $"Error processing job: {ex.Message}", Models.LogLevel.Error);
        }
    }

    private void CleanupProcessedJobsCache()
    {
        // Only cleanup if we exceed the max
        if (_processedOneOffJobs.Count <= MaxProcessedJobsToKeep)
            return;
        
        // Remove oldest entries (older than 24 hours or if over limit)
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var keysToRemove = _processedOneOffJobs
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();
        
        foreach (var key in keysToRemove)
        {
            _processedOneOffJobs.TryRemove(key, out _);
        }
        
        // If still over limit, remove oldest
        if (_processedOneOffJobs.Count > MaxProcessedJobsToKeep)
        {
            var excessKeys = _processedOneOffJobs
                .OrderBy(kvp => kvp.Value)
                .Take(_processedOneOffJobs.Count - MaxProcessedJobsToKeep)
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var key in excessKeys)
            {
                _processedOneOffJobs.TryRemove(key, out _);
            }
        }
    }

    private async Task ExecuteJobAsync(Job job)
    {
        try
        {
            _logger.LogDebug("Starting execution of job {JobId} ({JobName})", job.Id, job.Name);
            
            // Update status to Running - the API regenerates fresh ACR credentials when this happens
            await _apiClient.UpdateJobStatusAsync(job.Id, JobStatus.Running);
            await SendLogAsync(job.Id, $"Job execution started", Models.LogLevel.Info);

            // ALWAYS refresh the job after updating to Running to get fresh ACR credentials
            // The API regenerates credentials every time a job starts to ensure they're valid
            if (!string.IsNullOrEmpty(job.RegistryServer) || 
                (!string.IsNullOrEmpty(job.ContainerImage) && job.ContainerImage.Contains(".azurecr.io", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation("Job {JobId} uses ACR - fetching fresh credentials from API (current username: {Username})", 
                    job.Id, job.RegistryUsername ?? "[NONE]");
                    
                var refreshedJob = await _apiClient.GetJobAsync(job.Id);
                if (refreshedJob != null)
                {
                    // Update credential fields from refreshed job
                    var oldUsername = job.RegistryUsername;
                    job.RegistryUsername = refreshedJob.RegistryUsername;
                    job.RegistryPassword = refreshedJob.RegistryPassword;
                    job.RegistryServer = refreshedJob.RegistryServer;
                    
                    if (!string.IsNullOrEmpty(job.RegistryUsername))
                    {
                        _logger.LogInformation("Got fresh ACR credentials for job {JobId}: username changed from {OldUsername} to {NewUsername}", 
                            job.Id, oldUsername ?? "[NONE]", job.RegistryUsername);
                        await SendLogAsync(job.Id, $"Using fresh ACR credentials: {job.RegistryUsername}", Models.LogLevel.Info);
                    }
                    else
                    {
                        _logger.LogWarning("Job {JobId} has no ACR credentials after refresh - may fail to pull image", job.Id);
                    }
                }
                else
                {
                    _logger.LogWarning("Failed to refresh job {JobId} to get updated credentials", job.Id);
                }
            }

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

            // Execute the container with certificate, with retry on auth failure
            var success = await ExecuteJobWithCredentialRetryAsync(job, certificatePfx, certificatePassword);

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

    private async Task<bool> ExecuteJobWithCredentialRetryAsync(Job job, byte[]? certificatePfx, string? certificatePassword)
    {
        try
        {
            return await _containerExecutor.ExecuteJobAsync(
                job, 
                async (logMessage) => await SendLogAsync(job.Id, logMessage, Models.LogLevel.Info, "orchestrator"),
                async (logMessage) => await SendLogAsync(job.Id, logMessage, Models.LogLevel.Info, "container"),
                certificatePfx,
                certificatePassword);
        }
        catch (RegistryAuthenticationException authEx)
        {
            _logger.LogWarning(authEx, "Registry authentication failed for job {JobId}. Attempting to refresh credentials...", job.Id);
            await SendLogAsync(job.Id, "Registry authentication failed. Requesting credential refresh from API...", Models.LogLevel.Warning);
            
            // Request credential refresh from API
            var refreshedJob = await _apiClient.RefreshCredentialsAsync(job.Id);
            
            if (refreshedJob == null)
            {
                _logger.LogError("Failed to refresh credentials for job {JobId}", job.Id);
                await SendLogAsync(job.Id, "Failed to refresh credentials. Cannot retry.", Models.LogLevel.Error);
                throw;
            }
            
            _logger.LogInformation("Credentials refreshed for job {JobId}. Retrying with new credentials (username: {Username})", 
                job.Id, refreshedJob.RegistryUsername);
            await SendLogAsync(job.Id, $"Credentials refreshed. Retrying with new token: {refreshedJob.RegistryUsername}", Models.LogLevel.Info);
            
            // Update the job object with refreshed credentials
            job.RegistryServer = refreshedJob.RegistryServer;
            job.RegistryUsername = refreshedJob.RegistryUsername;
            job.RegistryPassword = refreshedJob.RegistryPassword;
            
            // Retry once with new credentials
            return await _containerExecutor.ExecuteJobAsync(
                job, 
                async (logMessage) => await SendLogAsync(job.Id, logMessage, Models.LogLevel.Info, "orchestrator"),
                async (logMessage) => await SendLogAsync(job.Id, logMessage, Models.LogLevel.Info, "container"),
                certificatePfx,
                certificatePassword);
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

    private void LogJobReceivedBanner(Job job)
    {
        var separator = new string('═', 60);
        var jobTypeIcon = job.JobType == JobType.Scheduled ? "[SCHEDULED]" : "[ONE-OFF]";
        var whatIfIndicator = job.IsWhatIf ? " [WHAT-IF]" : "";
        var pausedIndicator = job.IsPaused ? " [PAUSED]" : "";
        
        var banner = new System.Text.StringBuilder();
        banner.AppendLine();
        banner.AppendLine($"╔{separator}╗");
        banner.AppendLine($"║  {jobTypeIcon} JOB RECEIVED{whatIfIndicator}{pausedIndicator}");
        banner.AppendLine($"╠{separator}╣");
        banner.AppendLine($"║  Name:       {job.Name}");
        banner.AppendLine($"║  ID:         {job.Id}");
        banner.AppendLine($"║  Customer:   {job.Customer}");
        banner.AppendLine($"║  Type:       {job.JobType}");
        
        if (job.JobType == JobType.Scheduled && !string.IsNullOrEmpty(job.Schedule))
        {
            banner.AppendLine($"║  Schedule:   {job.Schedule}");
        }
        
        if (!string.IsNullOrEmpty(job.ScriptPath))
        {
            banner.AppendLine($"║  Script:     {job.ScriptPath}");
        }
        
        if (!string.IsNullOrEmpty(job.ContainerImage))
        {
            banner.AppendLine($"║  Image:      {job.ContainerImage}");
        }
        
        if (job.Parameters.Any())
        {
            banner.AppendLine($"║  Parameters: {job.Parameters.Count} defined");
        }
        
        banner.AppendLine($"║  Created:    {job.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");
        banner.AppendLine($"║  Status:     {job.Status}");
        banner.AppendLine($"║  Assigned:   {_orchestratorId}");
        banner.AppendLine($"╚{separator}╝");
        
        _logger.LogInformation("{JobBanner}", banner.ToString());
    }
}
