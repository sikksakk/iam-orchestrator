using IamOrchestrator;
using IamOrchestrator.Services;

var builder = Host.CreateApplicationBuilder(args);

// Configure services
builder.Services.AddHttpClient<IApiClient, ApiClient>(client =>
{
    var apiUrl = builder.Configuration["ApiSettings:BaseUrl"] ?? "http://localhost:5000";
    client.BaseAddress = new Uri(apiUrl);
});

builder.Services.AddSingleton<IContainerExecutor, ContainerExecutor>();
builder.Services.AddSingleton<IJobScheduler, JobScheduler>();
builder.Services.AddHostedService<OrchestratorWorker>();

var host = builder.Build();
host.Run();
