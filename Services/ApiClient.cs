using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using IamOrchestrator.Models;

namespace IamOrchestrator.Services;

public class ApiClient : IApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ApiClient> _logger;
    private readonly IConfiguration _configuration;
    private readonly JsonSerializerOptions _jsonOptions;
    private string? _authToken;
    private DateTime _tokenExpiration = DateTime.MinValue;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    public ApiClient(HttpClient httpClient, ILogger<ApiClient> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
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

            var response = await _httpClient.PostAsJsonAsync("/api/auth/login", loginRequest, _jsonOptions);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Authentication failed with status: {StatusCode}", response.StatusCode);
                return false;
            }

            var loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>(_jsonOptions);
            
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

    public async Task<List<Job>> GetPendingJobsAsync(string? customer = null)
    {
        try
        {
            if (!await EnsureAuthenticatedAsync())
            {
                _logger.LogWarning("Cannot get pending jobs: Not authenticated");
                return new List<Job>();
            }

            var url = "/api/jobs/pending";
            if (!string.IsNullOrEmpty(customer))
            {
                url += $"?customer={Uri.EscapeDataString(customer)}";
            }
            
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
            
            var jobs = await response.Content.ReadFromJsonAsync<List<Job>>(_jsonOptions);
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
            
            return await response.Content.ReadFromJsonAsync<Job>(_jsonOptions);
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

            var response = await _httpClient.PatchAsJsonAsync($"/api/jobs/{jobId}/status", status, _jsonOptions);
            
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Received 401, re-authenticating...");
                _authToken = null;
                if (await EnsureAuthenticatedAsync())
                {
                    response = await _httpClient.PatchAsJsonAsync($"/api/jobs/{jobId}/status", status, _jsonOptions);
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

            var response = await _httpClient.PostAsJsonAsync("/api/logs", logEntry, _jsonOptions);
            
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Received 401, re-authenticating...");
                _authToken = null;
                if (await EnsureAuthenticatedAsync())
                {
                    response = await _httpClient.PostAsJsonAsync("/api/logs", logEntry, _jsonOptions);
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

            var response = await _httpClient.PostAsJsonAsync("/api/orchestrators/heartbeat", orchestrator, _jsonOptions);
            
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Received 401, re-authenticating...");
                _authToken = null;
                if (await EnsureAuthenticatedAsync())
                {
                    response = await _httpClient.PostAsJsonAsync("/api/orchestrators/heartbeat", orchestrator, _jsonOptions);
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
}
