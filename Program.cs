using IamOrchestrator;
using IamOrchestrator.Services;
using Microsoft.Extensions.Http.Resilience;
using Polly;

var builder = Host.CreateApplicationBuilder(args);

// Configure services with Polly resilience policies
builder.Services.AddHttpClient<IApiClient, ApiClient>(client =>
{
    var apiUrl = builder.Configuration["ApiSettings:BaseUrl"] ?? "http://localhost:5000";
    client.BaseAddress = new Uri(apiUrl);
    client.Timeout = TimeSpan.FromSeconds(60); // Increased for retries
})
.AddStandardResilienceHandler(options =>
{
    // Configure retry policy - exponential backoff with jitter
    options.Retry.MaxRetryAttempts = 3;
    options.Retry.Delay = TimeSpan.FromSeconds(1);
    options.Retry.BackoffType = DelayBackoffType.Exponential;
    options.Retry.UseJitter = true;
    
    // Configure circuit breaker
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
    options.CircuitBreaker.FailureRatio = 0.5;
    options.CircuitBreaker.MinimumThroughput = 5;
    options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
    
    // Configure overall timeout
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);
});

builder.Services.AddSingleton<IContainerExecutor, ContainerExecutor>();
builder.Services.AddSingleton<IJobScheduler, JobScheduler>();
builder.Services.AddHostedService<OrchestratorWorker>();

var host = builder.Build();
host.Run();
