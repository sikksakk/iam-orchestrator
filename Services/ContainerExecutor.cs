using Docker.DotNet;
using Docker.DotNet.Models;
using IamOrchestrator.Models;
using System.Text;

namespace IamOrchestrator.Services;

public class ContainerExecutor : IContainerExecutor
{
    private readonly ILogger<ContainerExecutor> _logger;
    private readonly IConfiguration _configuration;
    private readonly DockerClient _dockerClient;

    public ContainerExecutor(ILogger<ContainerExecutor> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        
        var dockerEndpoint = configuration["ContainerSettings:DockerEndpoint"] ?? "npipe://./pipe/docker_engine";
        _dockerClient = new DockerClientConfiguration(new Uri(dockerEndpoint)).CreateClient();
    }

    public async Task<bool> ExecuteJobAsync(
        Job job, 
        Func<string, Task> orchestratorLogCallback, 
        Func<string, Task> containerOutputCallback,
        byte[]? certificatePfx = null,
        string? certificatePassword = null)
    {
        string? containerId = null;
        string? tempCertPath = null;
        
        try
        {
            await orchestratorLogCallback($"Starting job execution for {job.Name}");
            
            // Build environment variables from parameters
            var envVars = job.Parameters.Select(p => $"{p.Key}={p.Value}").ToList();
            
            // Add job metadata as environment variables
            envVars.Add($"JOB_ID={job.Id}");
            envVars.Add($"JOB_NAME={job.Name}");
            envVars.Add($"CUSTOMER={job.Customer}");
            envVars.Add($"SCRIPT_PATH={job.ScriptPath}");
            envVars.Add($"WHAT_IF={job.IsWhatIf}");

            // Handle certificate if provided
            var binds = new List<string>();
            if (certificatePfx != null && !string.IsNullOrEmpty(certificatePassword))
            {
                await orchestratorLogCallback("Certificate provided - preparing for container");
                
                // Write certificate to temp file
                tempCertPath = Path.Combine(Path.GetTempPath(), $"cert-{job.Id}.pfx");
                await File.WriteAllBytesAsync(tempCertPath, certificatePfx);
                
                // Mount certificate into container
                binds.Add($"{tempCertPath}:/tmp/client-cert.pfx:ro");
                
                // Add certificate environment variables
                envVars.Add("CLIENT_CERT_PATH=/tmp/client-cert.pfx");
                envVars.Add($"CLIENT_CERT_PASSWORD={certificatePassword}");
                
                _logger.LogInformation("Certificate mounted for job {JobId}", job.Id);
            }

            await orchestratorLogCallback($"Pulling container image: {job.ContainerImage}");
            
            // Debug: Log registry credential availability
            _logger.LogDebug("Registry credentials check - Server: '{Server}', Username: '{Username}', Password: {PasswordPresent}", 
                job.RegistryServer ?? "null", 
                job.RegistryUsername ?? "null", 
                string.IsNullOrEmpty(job.RegistryPassword) ? "NO" : "YES (length: " + job.RegistryPassword.Length + ")");
            
            // Pull the image
            AuthConfig? authConfig = null;
            if (!string.IsNullOrEmpty(job.RegistryUsername) && !string.IsNullOrEmpty(job.RegistryPassword))
            {
                var serverAddress = job.RegistryServer ?? job.ContainerImage.Split('/')[0];
                authConfig = new AuthConfig
                {
                    ServerAddress = serverAddress,
                    Username = job.RegistryUsername,
                    Password = job.RegistryPassword
                };
                
                await orchestratorLogCallback($"Authenticating to registry: {authConfig.ServerAddress} with user: {authConfig.Username}");
                _logger.LogDebug("AuthConfig created - ServerAddress: '{Server}', Username: '{User}', Password length: {Length}", 
                    authConfig.ServerAddress, authConfig.Username, authConfig.Password?.Length ?? 0);
                
                // Additional debug: log the image parts
                var imageParts = job.ContainerImage.Split('/');
                _logger.LogDebug("Image analysis - Full: '{Image}', First part: '{FirstPart}', Parts count: {Count}", 
                    job.ContainerImage, imageParts[0], imageParts.Length);
            }
            else
            {
                await orchestratorLogCallback("No registry credentials provided - attempting unauthenticated pull");
                _logger.LogWarning("Missing credentials - Username present: {UsernamePresent}, Password present: {PasswordPresent}", 
                    !string.IsNullOrEmpty(job.RegistryUsername), !string.IsNullOrEmpty(job.RegistryPassword));
            }
            
            // Parse image name properly - ensure registry server is included
            var fullImageName = job.ContainerImage.TrimStart('/');
            
            // If registry server is provided and not already in the image name, prepend it
            if (!string.IsNullOrEmpty(job.RegistryServer) && !fullImageName.StartsWith(job.RegistryServer))
            {
                fullImageName = $"{job.RegistryServer}/{fullImageName}";
            }
            
            _logger.LogInformation("Using full image name: {FullImageName} (original: {OriginalImage})", 
                fullImageName, job.ContainerImage);
            
            try
            {
                string fromImage;
                string tag;
                
                if (fullImageName.Contains(':'))
                {
                    var parts = fullImageName.Split(':');
                    fromImage = parts[0];
                    tag = parts[1];
                }
                else
                {
                    fromImage = fullImageName;
                    tag = "latest";
                }
                
                _logger.LogDebug("Calling Docker API CreateImageAsync - FromImage: '{FromImage}', Tag: '{Tag}', AuthConfig: {HasAuth}", 
                    fromImage, tag, authConfig != null ? "YES" : "NO");
                
                await _dockerClient.Images.CreateImageAsync(
                    new ImagesCreateParameters
                    {
                        FromImage = fromImage,
                        Tag = tag
                    },
                    authConfig,
                    new Progress<JSONMessage>(message =>
                    {
                        if (!string.IsNullOrEmpty(message.Status))
                        {
                            _logger.LogDebug("Docker pull: {Status} {Progress}", message.Status, message.ProgressMessage ?? "");
                        }
                        if (!string.IsNullOrEmpty(message.ErrorMessage))
                        {
                            _logger.LogError("Docker pull error: {Error}", message.ErrorMessage);
                        }
                    }));
                
                await orchestratorLogCallback("Image pull completed successfully");
                _logger.LogDebug("Successfully pulled image {Image}", fullImageName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pull image {Image}. Exception type: {ExceptionType}", 
                    fullImageName, ex.GetType().Name);
                await orchestratorLogCallback($"ERROR: Failed to pull image: {ex.Message}");
                
                if (ex.Message.Contains("unauthorized") || ex.Message.Contains("UNAUTHORIZED"))
                {
                    _logger.LogError("Authentication failed! Credentials may be incorrect or expired.");
                    await orchestratorLogCallback("AUTHENTICATION FAILED - Check registry username and password");
                }
                
                // Check if image exists locally using the full image name
                try
                {
                    await _dockerClient.Images.InspectImageAsync(fullImageName);
                    await orchestratorLogCallback("Image exists locally, will attempt to use it");
                    _logger.LogWarning("Using existing local image {Image} after pull failure", fullImageName);
                }
                catch
                {
                    await orchestratorLogCallback("ERROR: Image does not exist locally and pull failed");
                    throw new InvalidOperationException(
                        $"Failed to pull image '{fullImageName}' and it does not exist locally. " +
                        $"Error: {ex.Message}", ex);
                }
            }

            await orchestratorLogCallback("Creating container...");

            // Create container using the full image name
            var createResponse = await _dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = fullImageName,
                Env = envVars,
                HostConfig = new HostConfig
                {
                    AutoRemove = true,
                    Binds = binds.Any() ? binds : null
                },
                Name = $"iam-job-{job.Id}"
            });

            containerId = createResponse.ID;
            await orchestratorLogCallback($"Container created with ID: {containerId[..12]}");

            // Start container
            await _dockerClient.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
            await orchestratorLogCallback("Container started successfully");

            // Wait for container to complete
            var timeout = _configuration.GetValue<int>("ContainerSettings:DefaultTimeout", 3600);
            var exitCode = await WaitForContainerAsync(containerId, timeout, containerOutputCallback);

            if (exitCode == 0)
            {
                await orchestratorLogCallback($"Job completed successfully with exit code {exitCode}");
                return true;
            }
            else
            {
                await orchestratorLogCallback($"Job failed with exit code {exitCode}");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing job {JobId}", job.Id);
            await orchestratorLogCallback($"Error executing job: {ex.Message}");
            return false;
        }
        finally
        {
            // Cleanup temp certificate file
            if (!string.IsNullOrEmpty(tempCertPath) && File.Exists(tempCertPath))
            {
                try
                {
                    File.Delete(tempCertPath);
                    _logger.LogDebug("Deleted temporary certificate file: {Path}", tempCertPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temporary certificate file: {Path}", tempCertPath);
                }
            }
            
            // Cleanup
            if (containerId != null)
            {
                try
                {
                    await _dockerClient.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters
                    {
                        Force = true
                    });
                }
                catch (Docker.DotNet.DockerContainerNotFoundException)
                {
                    // Container already removed, this is fine
                    _logger.LogDebug("Container {ContainerId} already removed", containerId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove container {ContainerId}", containerId);
                }
            }
        }
    }

    private async Task<long> WaitForContainerAsync(string containerId, int timeoutSeconds, Func<string, Task> containerOutputCallback)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        
        try
        {
            // Wait for container to exit
            var waitTask = _dockerClient.Containers.WaitContainerAsync(containerId, cts.Token);
            
            // Get logs
            var logStream = await _dockerClient.Containers.GetContainerLogsAsync(
                containerId,
                true, // tty
                new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Follow = true
                },
                cts.Token);

            // Read logs in a background task using the multiplexed stream
            _ = Task.Run(async () =>
            {
                try
                {
                    var buffer = new byte[4096];
                    while (true)
                    {
                        var result = await logStream.ReadOutputAsync(buffer, 0, buffer.Length, cts.Token);
                        if (result.Count == 0) break;
                        
                        var logMessage = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (!string.IsNullOrWhiteSpace(logMessage))
                        {
                            await containerOutputCallback(logMessage.Trim());
                        }
                    }
                }
                catch { }
            }, cts.Token);

            var waitResponse = await waitTask;
            return waitResponse.StatusCode;
        }
        catch (OperationCanceledException)
        {
            await containerOutputCallback($"Container execution timed out after {timeoutSeconds} seconds");
            return -1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error waiting for container {ContainerId}", containerId);
            await containerOutputCallback($"Error monitoring container: {ex.Message}");
            return -1;
        }
    }
}
