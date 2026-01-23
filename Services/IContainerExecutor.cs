using IamOrchestrator.Models;

namespace IamOrchestrator.Services;

public interface IContainerExecutor
{
    Task<bool> ExecuteJobAsync(
        Job job, 
        Func<string, Task> orchestratorLogCallback, 
        Func<string, Task> containerOutputCallback,
        byte[]? certificatePfx = null,
        string? certificatePassword = null);
}
