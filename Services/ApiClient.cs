using System.Net.Http.Json;
using System.Text.Json;
using IamOrchestrator.Models;

namespace IamOrchestrator.Services;

public class ApiClient : IApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ApiClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public ApiClient(HttpClient httpClient, ILogger<ApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
    }

    public async Task<List<Job>> GetPendingJobsAsync(string? customer = null)
    {
        try
        {
            var url = "/api/jobs/pending";
            if (!string.IsNullOrEmpty(customer))
            {
                url += $"?customer={Uri.EscapeDataString(customer)}";
            }
            
            var response = await _httpClient.GetAsync(url);
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
            var response = await _httpClient.GetAsync($"/api/jobs/{jobId}");
            
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
            var response = await _httpClient.PatchAsJsonAsync($"/api/jobs/{jobId}/status", status, _jsonOptions);
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
            var response = await _httpClient.PostAsJsonAsync("/api/logs", logEntry, _jsonOptions);
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
            var response = await _httpClient.PostAsJsonAsync("/api/orchestrators/heartbeat", orchestrator, _jsonOptions);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send heartbeat");
            throw;
        }
    }
}
