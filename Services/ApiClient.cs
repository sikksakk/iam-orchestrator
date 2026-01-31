using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using IamOrchestrator.Models;

namespace IamOrchestrator.Services;

public class ApiClient : IApiClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ApiClient> _logger;
    private readonly IConfiguration _configuration;
    private string? _authToken;
    private DateTime _tokenExpiration = DateTime.MinValue;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private bool _disposed;

    // Static JsonSerializerOptions - thread-safe and expensive to create
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public ApiClient(HttpClient httpClient, ILogger<ApiClient> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _authLock?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    private async Task<bool> EnsureAuthenticatedAsync()
    {
        // Check if token is still valid (with 5 minute buffer)
        if (!string.IsNullOrEmpty(_authToken) && DateTime.UtcNow < _tokenExpiration.AddMinutes(-5))
        {
            return true;
        }

        await _authLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (!string.IsNullOrEmpty(_authToken) && DateTime.UtcNow < _tokenExpiration.AddMinutes(-5))
            {
                return true;
            }

            _logger.LogInformation("Authenticating with API...");

            var username = _configuration["ApiSettings:Username"] ?? "admin";
            var password = _configuration["ApiSettings:Password"] ?? "IAM#2026!SecureP@ssw0rd";

            var loginRequest = new LoginRequest
            {
                Username = username,
                Password = password
            };

            var response = await _httpClient.PostAsJsonAsync("/api/auth/login", loginRequest, s_jsonOptions);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Authentication failed with status: {StatusCode}", response.StatusCode);
                return false;
            }

            var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>(s_jsonOptions);
            
            if (loginResponse == null || string.IsNullOrEmpty(loginResponse.Token))
            {
                _logger.LogError("Authentication failed: Invalid response");
                return false;
            }

            _authToken = loginResponse.Token;
            _tokenExpiration = loginResponse.ExpiresAt;
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);

            _logger.LogInformation("Successfully authenticated as {Username}, token expires at {ExpiresAt}", 
                loginResponse.Username, loginResponse.ExpiresAt);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to authenticate with API");
            return false;
        }
        finally
        {
            _authLock.Release();
        }
    }

    public async Task<List<Job>> GetPendingJobsAsync(string orchestratorId, string customer)
    {
        try
        {
            if (!await EnsureAuthenticatedAsync())
            {
                _logger.LogWarning("Cannot get pending jobs: Not authenticated");
                return new List<Job>();
            }

            var url = $"/api/jobs/pending?orchestratorId={Uri.EscapeDataString(orchestratorId)}&customer={Uri.EscapeDataString(customer)}";
            
            var response = await _httpClient.GetAsync(url);
            
            // Retry authentication if we get 401
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Received 401, re-authenticating...");
                _authToken = null;
                if (await EnsureAuthenticatedAsync())
                {
                    response = await _httpClient.GetAsync(url);
                }
            }
            
            response.EnsureSuccessStatusCode();
            
            var jobs = await response.Content.ReadFromJsonAsync<List<Job>>(s_jsonOptions);
            return jobs ?? new List<Job>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get pending jobs from API");
            return new List<Job>();
        }
    }

    public async Task<Job?> GetJobAsync(Guid jobId)
    {
        try
        {
            if (!await EnsureAuthenticatedAsync())
            {
                _logger.LogWarning("Cannot get job: Not authenticated");
                return null;
            }

            var response = await _httpClient.GetAsync($"/api/jobs/{jobId}");
            
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Received 401, re-authenticating...");
                _authToken = null;
                if (await EnsureAuthenticatedAsync())
                {
                    response = await _httpClient.GetAsync($"/api/jobs/{jobId}");
                }
            }
            
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            
            return await response.Content.ReadFromJsonAsync<Job>(s_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get job {JobId} from API", jobId);
            return null;
        }
    }

    public async Task<bool> UpdateJobStatusAsync(Guid jobId, JobStatus status)
    {
        try
        {
            if (!await EnsureAuthenticatedAsync())
            {
                _logger.LogWarning("Cannot update job status: Not authenticated");
                return false;
            }

            var response = await _httpClient.PatchAsJsonAsync($"/api/jobs/{jobId}/status", status, s_jsonOptions);
            
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Received 401, re-authenticating...");
                _authToken = null;
                if (await EnsureAuthenticatedAsync())
                {
                    response = await _httpClient.PatchAsJsonAsync($"/api/jobs/{jobId}/status", status, s_jsonOptions);
                }
            }
            
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update job {JobId} status to {Status}", jobId, status);
            return false;
        }
    }

    public async Task<bool> SendLogAsync(LogEntry logEntry)
    {
        try
        {
            if (!await EnsureAuthenticatedAsync())
            {
                _logger.LogWarning("Cannot send log: Not authenticated");
                return false;
            }

            var response = await _httpClient.PostAsJsonAsync("/api/logs", logEntry, s_jsonOptions);
            
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Received 401, re-authenticating...");
                _authToken = null;
                if (await EnsureAuthenticatedAsync())
                {
                    response = await _httpClient.PostAsJsonAsync("/api/logs", logEntry, s_jsonOptions);
                }
            }
            
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send log for job {JobId}", logEntry.JobId);
            return false;
        }
    }

    public async Task SendHeartbeatAsync(Orchestrator orchestrator)
    {
        try
        {
            if (!await EnsureAuthenticatedAsync())
            {
                _logger.LogWarning("Cannot send heartbeat: Not authenticated");
                return;
            }

            var response = await _httpClient.PostAsJsonAsync("/api/orchestrators/heartbeat", orchestrator, s_jsonOptions);
            
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Received 401, re-authenticating...");
                _authToken = null;
                if (await EnsureAuthenticatedAsync())
                {
                    response = await _httpClient.PostAsJsonAsync("/api/orchestrators/heartbeat", orchestrator, s_jsonOptions);
                }
            }
            
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send heartbeat");
            throw;
        }
    }

    public async Task<CertificateResponse?> GetCertificateAsync(string customerName)
    {
        try
        {
            if (!await EnsureAuthenticatedAsync())
            {
                _logger.LogWarning("Cannot get certificate: Not authenticated");
                return null;
            }

            var response = await _httpClient.GetAsync($"/api/certificates/{Uri.EscapeDataString(customerName)}");
            
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Received 401, re-authenticating...");
                _authToken = null;
                if (await EnsureAuthenticatedAsync())
                {
                    response = await _httpClient.GetAsync($"/api/certificates/{Uri.EscapeDataString(customerName)}");
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get certificate for {Customer}: {StatusCode}", 
                    customerName, response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<CertificateResponse>(s_jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get certificate for {Customer}", customerName);
            return null;
        }
    }

    public async Task<bool> CheckForUpdateAsync(string orchestratorId)
    {
        try
        {
            if (!await EnsureAuthenticatedAsync())
            {
                _logger.LogWarning("Cannot check for update: Not authenticated");
                return false;
            }

            var url = $"/api/orchestrators/{Uri.EscapeDataString(orchestratorId)}/update-status";
            _logger.LogDebug("Checking update status at: {Url}", url);
            
            var response = await _httpClient.GetAsync(url);
            _logger.LogDebug("Update status response: {StatusCode}", response.StatusCode);
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("Update status response body: {Content}", content);
                
                var result = await response.Content.ReadFromJsonAsync<UpdateStatusResponse>(s_jsonOptions);
                _logger.LogDebug("Parsed pendingUpdate value: {PendingUpdate}", result?.PendingUpdate);
                return result?.PendingUpdate ?? false;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug("Orchestrator {OrchestratorId} not found in API", orchestratorId);
            }
            
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for update");
            return false;
        }
    }

    public async Task AcknowledgeUpdateAsync(string orchestratorId)
    {
        try
        {
            if (!await EnsureAuthenticatedAsync())
            {
                return;
            }

            await _httpClient.PostAsync($"/api/orchestrators/{Uri.EscapeDataString(orchestratorId)}/update-ack", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to acknowledge update");
        }
    }

    public async Task<Job?> RefreshCredentialsAsync(Guid jobId)
    {
        try
        {
            if (!await EnsureAuthenticatedAsync())
            {
                _logger.LogWarning("Cannot refresh credentials: Not authenticated");
                return null;
            }

            _logger.LogInformation("Requesting credential refresh for job {JobId}", jobId);
            
            var response = await _httpClient.PostAsync($"/api/jobs/{jobId}/refresh-credentials", null);
            
            // Retry authentication if we get 401
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Received 401, re-authenticating...");
                _authToken = null;
                if (await EnsureAuthenticatedAsync())
                {
                    response = await _httpClient.PostAsync($"/api/jobs/{jobId}/refresh-credentials", null);
                }
            }

            if (response.IsSuccessStatusCode)
            {
                var job = await response.Content.ReadFromJsonAsync<Job>(s_jsonOptions);
                _logger.LogInformation("Successfully refreshed credentials for job {JobId}", jobId);
                return job;
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Failed to refresh credentials for job {JobId}: {StatusCode} - {Error}", 
                    jobId, response.StatusCode, error);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while refreshing credentials for job {JobId}", jobId);
            return null;
        }
    }
}

public class UpdateStatusResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("pendingUpdate")]
    public bool PendingUpdate { get; set; }
}
