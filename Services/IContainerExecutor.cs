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

/// <summary>
/// Exception thrown when registry authentication fails during image pull.
/// This indicates that credentials may be expired or invalid.
/// </summary>
public class RegistryAuthenticationException : Exception
{
    public RegistryAuthenticationException(string message) : base(message) { }
    public RegistryAuthenticationException(string message, Exception innerException) : base(message, innerException) { }
}
