using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using FactorioLibrary.Models;
using FactorioLibrary.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
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
    private readonly IServiceScopeFactory _scopeFactory;

    // Maps instance ID to Docker Container ID
    private readonly ConcurrentDictionary<int, string> _runningContainers = new();

    // Cache stats for 2.5 seconds to prevent multiple clients from hammering Docker API
    private readonly ConcurrentDictionary<int, (DateTime FetchedAt, ServerStats Stats)> _statsCache = new();

    public InstanceManager(IConfiguration configuration, RconService rconService, IServiceScopeFactory scopeFactory)
    {
        _rconService = rconService;
        _scopeFactory = scopeFactory;
        // Use named pipes on Windows, unix socket on Linux/Unraid
        Uri dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
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
            IList<ContainerListResponse> containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = false });
            foreach (ContainerListResponse? c in containers)
            {
                if (c.Names != null && c.Names.Any(n => n.StartsWith("/factorio_server_")))
                {
                    string name = c.Names.First(n => n.StartsWith("/factorio_server_"));
                    if (int.TryParse(name.Replace("/factorio_server_", ""), out int id))
                        _runningContainers.TryAdd(id, c.ID);
                }
            }
        }
        catch { }
    }

    public async Task<(bool Success, bool CleanedCorruptSave)> StartInstanceAsync(ServerInstance instance)
    {
        if (_runningContainers.ContainsKey(instance.Id))
            return (false, false); // Already tracked as running

        string containerName = $"factorio_server_{instance.Id}";
        string imageTag = string.IsNullOrWhiteSpace(instance.AssignedVersion) ? "latest" : instance.AssignedVersion;
        string image = $"factoriotools/factorio:{imageTag}";

        try
        {
            Console.WriteLine($"[Instance {instance.Id}] Starting instance. Target Image: {image}");

            // 1. Ensure the image is pulled
            Console.WriteLine($"[Instance {instance.Id}] Ensuring image is pulled...");
            await _dockerClient.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = image }, null, new Progress<JSONMessage>());

            // 2. Remove existing container with the same name if it exists (but is stopped)
            Console.WriteLine($"[Instance {instance.Id}] Checking for existing containers named {containerName}...");
            IList<ContainerListResponse> existingContainers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });

            foreach (ContainerListResponse? c in existingContainers)
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
                        return (true, false);
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
                string localDataPath = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? $"/factorio/{instance.Id}" : instanceHostPath;

                string configPath = System.IO.Path.Combine(localDataPath, "config");
                System.IO.Directory.CreateDirectory(configPath);
                await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(configPath, "rconpw"), instance.RconPassword);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not pre-create rconpw file: {ex.Message}");
            }

            // 4. Configure save file loading behavior
            string savesDir = GetSavesDirectory(instance.Id);
            bool cleanedCorruptSave = false;
            
            // Clean up any orphaned .tmp.zip files from unclean shutdowns before checking saves
            // If we don't do this, factoriotools/factorio will pick the .tmp.zip as the latest save and fail to boot.
            if (Directory.Exists(savesDir))
            {
                foreach (string tmpFile in Directory.GetFiles(savesDir, "*.tmp.zip"))
                {
                    try { File.Delete(tmpFile); cleanedCorruptSave = true; } catch { Console.WriteLine($"Could not delete {tmpFile}"); }
                }
            }
            
            bool hasSaves = Directory.Exists(savesDir) && Directory.GetFiles(savesDir, "*.zip").Any();

            string loadLatest = "false";
            if (string.IsNullOrEmpty(instance.ActiveSaveName))
            {
                if (hasSaves)
                    loadLatest = "true";
                else
                    instance.ActiveSaveName = instance.Name + ".zip"; // just for this startup logic
            }

            List<string> envVars =
            [
                $"PORT={instance.Port}",
                $"RCON_PORT={instance.RconPort}",
                $"RCON_PASSWORD={instance.RconPassword}",
                $"LOAD_LATEST_SAVE={loadLatest}"
            ];

            if (!string.IsNullOrEmpty(instance.ActiveSaveName))
            {
                // Factorio container expects SAVE_NAME without the .zip extension
                string saveNameWithoutExtension = instance.ActiveSaveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? instance.ActiveSaveName.Substring(0, instance.ActiveSaveName.Length - 4) : instance.ActiveSaveName;
                envVars.Add($"SAVE_NAME={saveNameWithoutExtension}");
            }

            Console.WriteLine($"[Instance {instance.Id}] Creating Docker container {containerName} with Port: {instance.Port}, RconPort: {instance.RconPort}...");

            CreateContainerResponse response = await _dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = image,
                Name = containerName,
                Env = envVars,
                HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        { "34197/udp", new List<PortBinding> { new() { HostPort = instance.Port.ToString() } } },
                        { "27015/tcp", new List<PortBinding> { new() { HostPort = instance.RconPort.ToString() } } }
                    },
                    Binds =
                    [
                        $"{instanceHostPath}:/factorio"
                    ]
                }
            });

            // 5. Start the container
            Console.WriteLine($"[Instance {instance.Id}] Starting Docker container {response.ID}...");
            bool started = await _dockerClient.Containers.StartContainerAsync(response.ID, null);

            if (started)
            {
                Console.WriteLine($"[Instance {instance.Id}] Successfully started!");
                _runningContainers.TryAdd(instance.Id, response.ID);
                return (true, cleanedCorruptSave);
            }

            Console.WriteLine($"[Instance {instance.Id}] Docker reported the container did not start successfully.");
            return (false, cleanedCorruptSave);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Instance {instance.Id}] FATAL ERROR during startup: {ex.GetType().Name}");
            Console.WriteLine($"[Instance {instance.Id}] Message: {ex.Message}");
            Console.WriteLine($"[Instance {instance.Id}] Stack Trace: {ex.StackTrace}");
            return (false, false);
        }
    }

    public async Task StopInstanceAsync(int instanceId)
    {
        if (_runningContainers.TryGetValue(instanceId, out string? containerId))
        {
            try
            {
                // Try clean RCON shutdown first to bypass Docker's buggy SIGTERM routing
                using IServiceScope scope = _scopeFactory.CreateScope();
                AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                GlobalSettingsService settingsService = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
                
                ServerInstance? instance = await dbContext.ServerInstances.FindAsync(instanceId);
                int timeoutSeconds = settingsService.GetSettings().ShutdownTimeoutSeconds;
                
                if (instance != null)
                {
                    Console.WriteLine($"[Instance {instanceId}] Initiating clean shutdown via RCON /quit...");
                    await _rconService.SendCommandAsync(instanceId, instance.RconPort, instance.RconPassword, "/quit");
                    
                    // Wait for Factorio to cleanly save and exit the container on its own
                    for (int i = 0; i < timeoutSeconds; i++)
                    {
                        try 
                        {
                            var c = await _dockerClient.Containers.InspectContainerAsync(containerId);
                            if (!c.State.Running) break;
                        } catch { break; } // Container was removed/stopped
                        
                        await Task.Delay(1000);
                    }
                }

                // Fallback catch-all to ensure the container is stopped if it hung
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
            IList<ContainerListResponse> existingContainers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });

            foreach (ContainerListResponse? c in existingContainers)
            {
                if (c.Names.Contains($"/{containerName}"))
                {
                    if (c.State == "running")
                        await _dockerClient.Containers.StopContainerAsync(c.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 60 });

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
        string localDataPath = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? $"/factorio/{instanceId}" : instanceHostPath;

        try
        {
            if (Directory.Exists(localDataPath))
                Directory.Delete(localDataPath, true);
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
                ContainerLogsParameters parameters = new()
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
                ContainerInspectResponse inspect = await _dockerClient.Containers.InspectContainerAsync(containerId);
                return inspect.NetworkSettings.IPAddress;
            }
            catch { }
        }
        return null;
    }

    public async Task<(bool Success, string Logs)> SyncModsWithSaveAsync(int instanceId, string saveName, string imageTag, int retryCount = 0)
    {
        string containerName = $"factorio_sync_{instanceId}_{Guid.NewGuid().ToString().Substring(0, 8)}";
        string image = $"factoriotools/factorio:{imageTag}";
        string instanceHostPath = $"{_hostBaseMountPath.TrimEnd('/', '\\')}/{instanceId}";

        try
        {
            await _dockerClient.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = image }, null, new Progress<JSONMessage>());

            CreateContainerResponse response = await _dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = image,
                Name = containerName,
                HostConfig = new HostConfig
                {
                    Binds = [$"{instanceHostPath}:/factorio"]
                },
                // Override the default entrypoint script so it doesn't try to boot a multiplayer server
                Entrypoint = ["/opt/factorio/bin/x64/factorio"],
                // Run the sync-mods command and immediately exit
                Cmd = ["--sync-mods", $"/factorio/saves/{saveName}", "--mod-directory", "/factorio/mods"]
            });

            await _dockerClient.Containers.StartContainerAsync(response.ID, null);

            // Wait for it to finish parsing and syncing
            await _dockerClient.Containers.WaitContainerAsync(response.ID);

            // Fetch the logs to return to the UI (especially if there's a version mismatch error)
            MultiplexedStream logsStream = await _dockerClient.Containers.GetContainerLogsAsync(response.ID, false, new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true
            });

            (string stdout, string stderr) logs = await logsStream.ReadOutputToEndAsync(default);
            string fullLog = logs.stdout + "\n" + logs.stderr;

            // Remove the temporary container
            await _dockerClient.Containers.RemoveContainerAsync(response.ID, new ContainerRemoveParameters { Force = true });

            // If it crashed or had an error, it often outputs Error or fails to write
            bool success = !fullLog.Contains("Error", StringComparison.OrdinalIgnoreCase);

            if (!success && retryCount < 5)
            {
                bool copiedRescueMod = false;
                string basePath = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? "/factorio" : _hostBaseMountPath.TrimEnd('/', '\\');
                string globalPath = Path.Combine(basePath, "global_mods");
                if (Directory.Exists(globalPath))
                {
                    var globalZips = Directory.GetFiles(globalPath, "*.zip");
                    foreach (string zip in globalZips)
                    {
                        string zipName = Path.GetFileName(zip);
                        int lastUnderscore = zipName.LastIndexOf('_');
                        if (lastUnderscore > 0)
                        {
                            string modName = zipName.Substring(0, lastUnderscore);
                            // If the error log mentions this mod
                            if (fullLog.Contains(modName, StringComparison.OrdinalIgnoreCase) || fullLog.Contains(zipName, StringComparison.OrdinalIgnoreCase))
                            {
                                string targetModsDir = Path.Combine(basePath, instanceId.ToString(), "mods");
                                if (!Directory.Exists(targetModsDir)) Directory.CreateDirectory(targetModsDir);
                                
                                string targetFile = Path.Combine(targetModsDir, zipName);
                                if (!File.Exists(targetFile))
                                {
                                    try
                                    {
                                        File.Copy(zip, targetFile, true);
                                        copiedRescueMod = true;
                                    }
                                    catch {}
                                }
                            }
                        }
                    }
                }
                
                if (copiedRescueMod)
                {
                    return await SyncModsWithSaveAsync(instanceId, saveName, imageTag, retryCount + 1);
                }
            }

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
        string localDataPath = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? $"/factorio/{instanceId}" : instanceHostPath;

        string configPath = Path.Combine(localDataPath, "config");
        string playerDataPath = Path.Combine(localDataPath, "player-data.json");

        try
        {
            if (Directory.Exists(configPath))
                Directory.Delete(configPath, true);
            if (File.Exists(playerDataPath))
                File.Delete(playerDataPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error resetting configs: {ex.Message}");
        }
    }

    public async Task<ServerStats> GetLiveStatsAsync(ServerInstance instance)
    {
        if (_statsCache.TryGetValue(instance.Id, out (DateTime FetchedAt, ServerStats Stats) cached) && (DateTime.UtcNow - cached.FetchedAt).TotalSeconds < 2.5)
            return cached.Stats;

        ServerStats stats = new() { IsOnline = false };

        if (_runningContainers.TryGetValue(instance.Id, out string? containerId))
        {
            stats.IsOnline = true;
            try
            {
                // 1. Get Docker Stats
                ContainerStatsParameters param = new() { Stream = false };
                ContainerStatsResponse? lastStats = null;
                SyncProgress<ContainerStatsResponse> progress = new(msg => lastStats = msg);
                await _dockerClient.Containers.GetContainerStatsAsync(containerId, param, progress, CancellationToken.None);

                if (lastStats != null)
                {
                    // RAM
                    stats.RamUsageMb = lastStats.MemoryStats.Usage / (1024 * 1024.0);
                    stats.RamLimitMb = lastStats.MemoryStats.Limit / (1024 * 1024.0);

                    // CPU
                    double cpuDelta = lastStats.CPUStats.CPUUsage.TotalUsage - lastStats.PreCPUStats.CPUUsage.TotalUsage;
                    double systemDelta = lastStats.CPUStats.SystemUsage - lastStats.PreCPUStats.SystemUsage;
                                          
                    if (systemDelta > 0.0 && cpuDelta > 0.0)
                    {
                        double onlineCpus = lastStats.CPUStats.OnlineCPUs;
                        stats.CpuPercentage = (cpuDelta / systemDelta) * onlineCpus * 100.0;
                    }
                }

                // 2. Get RCON Stats
                if (instance.RconPort > 0 && !string.IsNullOrEmpty(instance.RconPassword))
                    stats.OnlinePlayers = await _rconService.GetOnlinePlayersAsync(instance.Id, instance.RconPort, instance.RconPassword);
            }
            catch
            {
                // Ignore transient errors on stats pulling
            }
        }

        _statsCache[instance.Id] = (DateTime.UtcNow, stats);
        return stats;
    }

    public string GetSavesDirectory(int instanceId)
    {
        string instanceHostPath = Path.Combine(_hostBaseMountPath, instanceId.ToString());
        string localDataPath = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? $"/factorio/{instanceId}" : instanceHostPath;

        return Path.Combine(localDataPath, "saves");
    }

    public string GetModsDirectory(int instanceId)
    {
        string instanceHostPath = Path.Combine(_hostBaseMountPath, instanceId.ToString());
        string localDataPath = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? $"/factorio/{instanceId}" : instanceHostPath;

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

public class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly Action<T> _handler = handler;
    public void Report(T value) => _handler(value);
}
