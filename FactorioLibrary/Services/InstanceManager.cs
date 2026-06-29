using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FactorioLibrary.Models;
using Microsoft.Extensions.Configuration;

namespace FactorioLibrary.Services;

public class InstanceManager
{
    private readonly DockerClient _dockerClient;
    private readonly string _hostBaseMountPath;
    private readonly RconService _rconService;
    
    // Maps instance ID to Docker Container ID
    private readonly ConcurrentDictionary<int, string> _runningContainers = new();

    public InstanceManager(IConfiguration configuration, RconService rconService)
    {
        _rconService = rconService;
        // Use named pipes on Windows, unix socket on Linux/Unraid
        var dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");
            
        _dockerClient = new DockerClientConfiguration(dockerUri).CreateClient();
        
        // This is the path ON THE HOST OS where we store all Factorio data
        _hostBaseMountPath = configuration.GetValue<string>("HOST_BASE_MOUNT_PATH") 
            ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "C:\\FactorioServers" : "/mnt/user/appdata/factorio_manager/servers");
            
        // Auto-discover any already running containers
        _ = InitializeRunningContainersAsync();
    }
    
    private async Task InitializeRunningContainersAsync()
    {
        try
        {
            var containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = false });
            foreach (var c in containers)
            {
                if (c.Names != null && c.Names.Any(n => n.StartsWith("/factorio_server_")))
                {
                    string name = c.Names.First(n => n.StartsWith("/factorio_server_"));
                    if (int.TryParse(name.Replace("/factorio_server_", ""), out int id))
                    {
                        _runningContainers.TryAdd(id, c.ID);
                    }
                }
            }
        }
        catch { }
    }

    public async Task<bool> StartInstanceAsync(ServerInstance instance)
    {
        if (_runningContainers.ContainsKey(instance.Id)) 
            return false; // Already tracked as running

        string containerName = $"factorio_server_{instance.Id}";
        string imageTag = string.IsNullOrWhiteSpace(instance.AssignedVersion) ? "latest" : instance.AssignedVersion;
        string image = $"factoriotools/factorio:{imageTag}";

        try
        {
            Console.WriteLine($"[Instance {instance.Id}] Starting instance. Target Image: {image}");

            // 1. Ensure the image is pulled
            Console.WriteLine($"[Instance {instance.Id}] Ensuring image is pulled...");
            await _dockerClient.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image }, 
                null, 
                new Progress<JSONMessage>());

            // 2. Remove existing container with the same name if it exists (but is stopped)
            Console.WriteLine($"[Instance {instance.Id}] Checking for existing containers named {containerName}...");
            var existingContainers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = true });
                
            foreach (var c in existingContainers)
            {
                if (c.Names.Contains($"/{containerName}"))
                {
                    if (c.State != "running")
                    {
                        Console.WriteLine($"[Instance {instance.Id}] Found stopped container {c.ID}. Removing it...");
                        await _dockerClient.Containers.RemoveContainerAsync(c.ID, new ContainerRemoveParameters { Force = true });
                    }
                    else
                    {
                        Console.WriteLine($"[Instance {instance.Id}] Container {c.ID} is already running.");
                        _runningContainers.TryAdd(instance.Id, c.ID);
                        return true;
                    }
                }
            }

            // 3. Create host directory path for this specific instance
            string instanceHostPath = $"{_hostBaseMountPath.TrimEnd('/', '\\')}/{instance.Id}";
            Console.WriteLine($"[Instance {instance.Id}] Host Mount Path configured as: {instanceHostPath}");
            
            // Note: Docker will automatically create the host directory if it doesn't exist when the volume is mounted,
            // but we MUST write the rconpw file before starting because factoriotools/factorio does not use env vars for it!
            try
            {
                string localDataPath = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" 
                    ? $"/factorio/{instance.Id}" 
                    : instanceHostPath;
                    
                string configPath = System.IO.Path.Combine(localDataPath, "config");
                System.IO.Directory.CreateDirectory(configPath);
                await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(configPath, "rconpw"), instance.RconPassword);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not pre-create rconpw file: {ex.Message}");
            }

            // 4. Configure save file loading behavior
            var savesDir = GetSavesDirectory(instance.Id);
            bool hasSaves = Directory.Exists(savesDir) && Directory.GetFiles(savesDir, "*.zip").Any();

            var loadLatest = "false";
            if (string.IsNullOrEmpty(instance.ActiveSaveName))
            {
                if (hasSaves)
                {
                    loadLatest = "true";
                }
                else
                {
                    // No saves exist, and no active save specified. Force it to create one named after the instance.
                    instance.ActiveSaveName = instance.Name + ".zip"; // just for this startup logic
                }
            }

            var envVars = new List<string>
            {
                $"PORT={instance.Port}",
                $"RCON_PORT={instance.RconPort}",
                $"RCON_PASSWORD={instance.RconPassword}",
                $"LOAD_LATEST_SAVE={loadLatest}"
            };

            if (!string.IsNullOrEmpty(instance.ActiveSaveName))
            {
                // Factorio container expects SAVE_NAME without the .zip extension
                var saveNameWithoutExtension = instance.ActiveSaveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) 
                    ? instance.ActiveSaveName.Substring(0, instance.ActiveSaveName.Length - 4) 
                    : instance.ActiveSaveName;
                envVars.Add($"SAVE_NAME={saveNameWithoutExtension}");
            }

            Console.WriteLine($"[Instance {instance.Id}] Creating Docker container {containerName} with Port: {instance.Port}, RconPort: {instance.RconPort}...");
            
            var response = await _dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = image,
                Name = containerName,
                Env = envVars,
                HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        { "34197/udp", new List<PortBinding> { new PortBinding { HostPort = instance.Port.ToString() } } },
                        { "27015/tcp", new List<PortBinding> { new PortBinding { HostPort = instance.RconPort.ToString() } } }
                    },
                    Binds = new List<string>
                    {
                        $"{instanceHostPath}:/factorio"
                    }
                }
            });

            // 5. Start the container
            Console.WriteLine($"[Instance {instance.Id}] Starting Docker container {response.ID}...");
            bool started = await _dockerClient.Containers.StartContainerAsync(response.ID, null);
            
            if (started)
            {
                Console.WriteLine($"[Instance {instance.Id}] Successfully started!");
                _runningContainers.TryAdd(instance.Id, response.ID);
                return true;
            }
            
            Console.WriteLine($"[Instance {instance.Id}] Docker reported the container did not start successfully.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Instance {instance.Id}] FATAL ERROR during startup: {ex.GetType().Name}");
            Console.WriteLine($"[Instance {instance.Id}] Message: {ex.Message}");
            Console.WriteLine($"[Instance {instance.Id}] Stack Trace: {ex.StackTrace}");
            return false;
        }
    }

    public async Task StopInstanceAsync(int instanceId)
    {
        if (_runningContainers.TryGetValue(instanceId, out string? containerId))
        {
            try
            {
                await _dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 10 });
            }
            catch
            {
                // Ignore errors if container already stopped or removed
            }
            finally
            {
                _runningContainers.TryRemove(instanceId, out _);
            }
        }
    }

    public async Task DeleteInstanceDataAsync(int instanceId)
    {
        // 1. Stop and remove the container
        string containerName = $"factorio_server_{instanceId}";
        try
        {
            var existingContainers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = true });
                
            foreach (var c in existingContainers)
            {
                if (c.Names.Contains($"/{containerName}"))
                {
                    if (c.State == "running")
                    {
                        await _dockerClient.Containers.StopContainerAsync(c.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 10 });
                    }
                    await _dockerClient.Containers.RemoveContainerAsync(c.ID, new ContainerRemoveParameters { Force = true });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error removing container during delete: {ex.Message}");
        }
        finally
        {
            _runningContainers.TryRemove(instanceId, out _);
        }

        // 2. Delete host directory
        string instanceHostPath = Path.Combine(_hostBaseMountPath, instanceId.ToString());
        string localDataPath = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" 
            ? $"/factorio/{instanceId}" 
            : instanceHostPath;

        try
        {
            if (Directory.Exists(localDataPath))
            {
                Directory.Delete(localDataPath, true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error deleting host directory: {ex.Message}");
        }
    }

    public bool IsRunning(int instanceId)
    {
        // Simple in-memory check for UI responsiveness. 
        // A more robust implementation would poll the Docker API, but this is fine for now.
        return _runningContainers.ContainsKey(instanceId);
    }

    public async Task<MultiplexedStream?> GetLogStreamAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        if (_runningContainers.TryGetValue(instanceId, out string? containerId))
        {
            try
            {
                var parameters = new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Follow = true,
                    Tail = "100" // Get last 100 lines + follow new ones
                };
                
                return await _dockerClient.Containers.GetContainerLogsAsync(containerId, false, parameters, cancellationToken);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public async Task<string?> GetContainerIpAddressAsync(int instanceId)
    {
        if (_runningContainers.TryGetValue(instanceId, out string? containerId))
        {
            try
            {
                var inspect = await _dockerClient.Containers.InspectContainerAsync(containerId);
                return inspect.NetworkSettings.IPAddress;
            }
            catch { }
        }
        return null;
    }

    public async Task<(bool Success, string Logs)> SyncModsWithSaveAsync(int instanceId, string saveName, string imageTag)
    {
        string containerName = $"factorio_sync_{instanceId}_{Guid.NewGuid().ToString().Substring(0, 8)}";
        string image = $"factoriotools/factorio:{imageTag}";
        string instanceHostPath = $"{_hostBaseMountPath.TrimEnd('/', '\\')}/{instanceId}";

        try
        {
            await _dockerClient.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image }, 
                null, 
                new Progress<JSONMessage>());

            var response = await _dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = image,
                Name = containerName,
                HostConfig = new HostConfig
                {
                    Binds = new List<string> { $"{instanceHostPath}:/factorio" }
                },
                // Override the default entrypoint script so it doesn't try to boot a multiplayer server
                Entrypoint = new List<string> { "/opt/factorio/bin/x64/factorio" },
                // Run the sync-mods command and immediately exit
                Cmd = new List<string> { "--sync-mods", $"/factorio/saves/{saveName}", "--mod-directory", "/factorio/mods" }
            });

            await _dockerClient.Containers.StartContainerAsync(response.ID, null);

            // Wait for it to finish parsing and syncing
            await _dockerClient.Containers.WaitContainerAsync(response.ID);

            // Fetch the logs to return to the UI (especially if there's a version mismatch error)
            var logsStream = await _dockerClient.Containers.GetContainerLogsAsync(response.ID, false, new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true
            });
            
            var logs = await logsStream.ReadOutputToEndAsync(default);
            string fullLog = logs.stdout + "\n" + logs.stderr;
            
            // Remove the temporary container
            await _dockerClient.Containers.RemoveContainerAsync(response.ID, new ContainerRemoveParameters { Force = true });

            // If it crashed or had an error, it often outputs Error or fails to write
            bool success = !fullLog.Contains("Error", StringComparison.OrdinalIgnoreCase);

            return (success, fullLog.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void FactoryResetConfigs(int instanceId)
    {
        string instanceHostPath = Path.Combine(_hostBaseMountPath, instanceId.ToString());
        string localDataPath = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" 
            ? $"/factorio/{instanceId}" 
            : instanceHostPath;

        string configPath = Path.Combine(localDataPath, "config");
        string playerDataPath = Path.Combine(localDataPath, "player-data.json");
        
        try
        {
            if (Directory.Exists(configPath))
            {
                Directory.Delete(configPath, true);
            }
            if (File.Exists(playerDataPath))
            {
                File.Delete(playerDataPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error resetting configs: {ex.Message}");
        }
    }

    public async Task<ServerStats> GetLiveStatsAsync(ServerInstance instance)
    {
        var stats = new ServerStats { IsOnline = false };

        if (_runningContainers.TryGetValue(instance.Id, out string? containerId))
        {
            stats.IsOnline = true;
            try
            {
                // 1. Get Docker Stats
                // GetContainerStatsAsync returns a stream of stats objects if Stream=true, or just one if Stream=false
                // In DotNet 3.125+, it returns an IObservable, or Stream. 
                // However, there is no direct easy way to parse the stream payload without doing a manual read for one snapshot if Stream=false.
                var param = new ContainerStatsParameters { Stream = false };
                var statsResponse = await _dockerClient.Containers.GetContainerStatsAsync(containerId, param, CancellationToken.None);
                
                // Usually stream=false returns a single stream chunk which is JSON. But Docker.DotNet maps this to a Stream that emits JSON strings.
                // An easier way is just to read the stream and deserialize.
                using var reader = new System.IO.StreamReader(statsResponse);
                string json = await reader.ReadLineAsync() ?? "";
                if (!string.IsNullOrEmpty(json))
                {
                    var doc = System.Text.Json.JsonDocument.Parse(json);
                    
                    // RAM
                    if (doc.RootElement.TryGetProperty("memory_stats", out var memStats))
                    {
                        if (memStats.TryGetProperty("usage", out var usageProp))
                        {
                            stats.RamUsageMb = usageProp.GetDouble() / (1024 * 1024);
                        }
                        if (memStats.TryGetProperty("limit", out var limitProp))
                        {
                            stats.RamLimitMb = limitProp.GetDouble() / (1024 * 1024);
                        }
                    }

                    // CPU
                    if (doc.RootElement.TryGetProperty("cpu_stats", out var cpuStats) &&
                        doc.RootElement.TryGetProperty("precpu_stats", out var preCpuStats))
                    {
                        var cpuDelta = cpuStats.GetProperty("cpu_usage").GetProperty("total_usage").GetDouble() -
                                       preCpuStats.GetProperty("cpu_usage").GetProperty("total_usage").GetDouble();
                        var systemDelta = cpuStats.GetProperty("system_cpu_usage").GetDouble() -
                                          preCpuStats.GetProperty("system_cpu_usage").GetDouble();
                                          
                        if (systemDelta > 0.0 && cpuDelta > 0.0)
                        {
                            var onlineCpus = cpuStats.GetProperty("online_cpus").GetDouble();
                            stats.CpuPercentage = (cpuDelta / systemDelta) * onlineCpus * 100.0;
                        }
                    }
                }

                // 2. Get RCON Stats
                if (instance.RconPort > 0 && !string.IsNullOrEmpty(instance.RconPassword))
                {
                    stats.OnlinePlayers = await _rconService.GetOnlinePlayersAsync(instance.Id, instance.RconPort, instance.RconPassword);
                }
            }
            catch
            {
                // Ignore transient errors on stats pulling
            }
        }

        return stats;
    }

    public string GetSavesDirectory(int instanceId)
    {
        string instanceHostPath = Path.Combine(_hostBaseMountPath, instanceId.ToString());
        string localDataPath = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" 
            ? $"/factorio/{instanceId}" 
            : instanceHostPath;
            
        return Path.Combine(localDataPath, "saves");
    }

    public string GetModsDirectory(int instanceId)
    {
        string instanceHostPath = Path.Combine(_hostBaseMountPath, instanceId.ToString());
        string localDataPath = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" 
            ? $"/factorio/{instanceId}" 
            : instanceHostPath;
            
        return Path.Combine(localDataPath, "mods");
    }

    public string GetConfigDirectory(int instanceId)
    {
        string instanceHostPath = Path.Combine(_hostBaseMountPath, instanceId.ToString());
        string localDataPath = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" 
            ? $"/factorio/{instanceId}" 
            : instanceHostPath;
            
        return Path.Combine(localDataPath, "config");
    }
}
