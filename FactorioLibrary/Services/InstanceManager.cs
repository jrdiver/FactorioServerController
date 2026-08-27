using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using FactorioLibrary.Data;
using FactorioLibrary.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactorioLibrary.Services;

public class InstanceManager
{
    private readonly DockerClient dockerClient;
    private readonly string hostBaseMountPath;
    private readonly string internalBaseMountPath;
    private readonly string hostDataPath;
    private readonly string internalDataPath;
    private readonly RconService rconService;
    private readonly IServiceScopeFactory scopeFactory;

    // Maps instance ID to Docker Container ID
    private readonly ConcurrentDictionary<int, string> runningContainers = new();

    // Cache stats for 2.5 seconds to prevent multiple clients from hammering Docker API
    private readonly ConcurrentDictionary<int, (DateTime FetchedAt, ServerStats Stats)> statsCache = new();
    
    private readonly Timer healthCheckTimer;

    public InstanceManager(IConfiguration configuration, RconService rconService, IServiceScopeFactory scopeFactory)
    {
        this.rconService = rconService;
        this.scopeFactory = scopeFactory;
        // Use named pipes on Windows, unix socket on Linux/Unraid
        Uri dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");

        dockerClient = new DockerClientConfiguration(dockerUri).CreateClient();

        // Single unified path on the host for all app-data and instances
        hostDataPath = configuration.GetValue<string>("HOST_DATA_PATH");
        if (string.IsNullOrWhiteSpace(hostDataPath))
            hostDataPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"C:\Factorio" : "/data";

        hostBaseMountPath = Path.Combine(hostDataPath, "instances");
        
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
        {
            internalDataPath = "/data";
            internalBaseMountPath = "/data/instances";
        }
        else
        {
            internalDataPath = hostDataPath;
            internalBaseMountPath = hostBaseMountPath;
        }

        healthCheckTimer = new(async _ => await CheckContainerHealthAsync(), null, 5000, 5000);

        // Auto-discover any already running containers
        _ = InitializeRunningContainersAsync();
    }

    private string GetLocalDataPath(int instanceId) => Path.Combine(internalBaseMountPath, instanceId.ToString());
    private string GetInstanceHostPath(int instanceId) => Path.Combine(hostBaseMountPath, instanceId.ToString());

    private async Task CheckContainerHealthAsync()
    {
        foreach (KeyValuePair<int, string> kvp in runningContainers.ToArray())
        {
            try
            {
                ContainerInspectResponse c = await dockerClient.Containers.InspectContainerAsync(kvp.Value);
                if (!c.State.Running && !c.State.Restarting)
                {
                    runningContainers.TryRemove(kvp.Key, out _);
                }
            }
            catch
            {
                runningContainers.TryRemove(kvp.Key, out _);
            }
        }
    }

    private async Task InitializeRunningContainersAsync()
    {
        try
        {
            IList<ContainerListResponse> containers = await dockerClient.Containers.ListContainersAsync(new() { All = false });
            foreach (ContainerListResponse? c in containers)
            {
                if (c.Names != null && c.Names.Any(n => n.StartsWith("/factorio_server_")))
                {
                    string name = c.Names.First(n => n.StartsWith("/factorio_server_"));
                    if (int.TryParse(name.Replace("/factorio_server_", ""), out int id))
                        runningContainers.TryAdd(id, c.ID);
                }
            }
        }
        catch { }
    }

    public async Task<(bool Success, bool CleanedCorruptSave)> StartInstanceAsync(ServerInstance instance)
    {
        if (runningContainers.ContainsKey(instance.Id))
            return (false, false); // Already tracked as running

        string containerName = $"factorio_server_{instance.Id}";
        string imageTag = string.IsNullOrWhiteSpace(instance.AssignedVersion) ? "latest" : instance.AssignedVersion;
        string image = $"factoriotools/factorio:{imageTag}";

        try
        {
            Console.WriteLine($"[Instance {instance.Id}] Starting instance. Target Image: {image}");

            // 1. Ensure the image is pulled
            Console.WriteLine($"[Instance {instance.Id}] Ensuring image is pulled...");
            await dockerClient.Images.CreateImageAsync(new() { FromImage = image }, null, new Progress<JSONMessage>());

            // 2. Remove existing container with the same name if it exists (but is stopped)
            Console.WriteLine($"[Instance {instance.Id}] Checking for existing containers named {containerName}...");
            IList<ContainerListResponse> existingContainers = await dockerClient.Containers.ListContainersAsync(new() { All = true });

            foreach (ContainerListResponse? c in existingContainers)
            {
                if (c.Names.Contains($"/{containerName}"))
                {
                    if (c.State != "running")
                    {
                        Console.WriteLine($"[Instance {instance.Id}] Found stopped container {c.ID}. Removing it...");
                        await dockerClient.Containers.RemoveContainerAsync(c.ID, new() { Force = true });
                    }
                    else
                    {
                        Console.WriteLine($"[Instance {instance.Id}] Container {c.ID} is already running.");
                        runningContainers.TryAdd(instance.Id, c.ID);
                        return (true, false);
                    }
                }
            }

            // 3. Create host directory path for this specific instance
            string instanceHostPath = $"{hostBaseMountPath.TrimEnd('/', '\\')}/{instance.Id}";
            Console.WriteLine($"[Instance {instance.Id}] Host Mount Path configured as: {instanceHostPath}");

            // Note: Docker will automatically create the host directory if it doesn't exist when the volume is mounted,
            // but we MUST write the rconpw file before starting because factoriotools/factorio does not use env vars for it!
            try
            {
                string localDataPath = GetLocalDataPath(instance.Id);

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
            string generateNewSave = "false";
            
            if (string.IsNullOrEmpty(instance.ActiveSaveName))
            {
                if (hasSaves)
                {
                    loadLatest = "true";
                }
                else
                {
                    instance.ActiveSaveName = instance.Name + ".zip"; // just for this startup logic
                    generateNewSave = "true";
                }
            }
            else if (!hasSaves || !File.Exists(Path.Combine(savesDir, instance.ActiveSaveName)))
            {
                // If they have an ActiveSaveName specified but the file itself doesn't actually exist, generate it
                generateNewSave = "true";
            }

            List<string> envVars =
            [
                $"PORT={instance.Port}",
                $"RCON_PORT={instance.RconPort}",
                $"RCON_PASSWORD={instance.RconPassword}",
                $"LOAD_LATEST_SAVE={loadLatest}",
                $"GENERATE_NEW_SAVE={generateNewSave}"
            ];

            if (!string.IsNullOrEmpty(instance.ActiveSaveName))
            {
                // Factorio container expects SAVE_NAME without the .zip extension
                string saveNameWithoutExtension = instance.ActiveSaveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? instance.ActiveSaveName.Substring(0, instance.ActiveSaveName.Length - 4) : instance.ActiveSaveName;
                envVars.Add($"SAVE_NAME={saveNameWithoutExtension}");
            }

            Console.WriteLine($"[Instance {instance.Id}] Creating Docker container {containerName} with Port: {instance.Port}, RconPort: {instance.RconPort}...");

            CreateContainerResponse response = await dockerClient.Containers.CreateContainerAsync(new()
            {
                Image = image,
                Name = containerName,
                Env = envVars,
                HostConfig = new()
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
            bool started = await dockerClient.Containers.StartContainerAsync(response.ID, null);

            if (started)
            {
                Console.WriteLine($"[Instance {instance.Id}] Successfully started!");
                runningContainers.TryAdd(instance.Id, response.ID);
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
        if (runningContainers.TryGetValue(instanceId, out string? containerId))
        {
            try
            {
                // Try clean RCON shutdown first to bypass Docker's buggy SIGTERM routing
                using IServiceScope scope = scopeFactory.CreateScope();
                AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                GlobalSettingsService settingsService = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
                
                ServerInstance? instance = await dbContext.ServerInstances.FindAsync(instanceId);
                int timeoutSeconds = settingsService.GetSettings().ShutdownTimeoutSeconds;
                
                if (instance != null)
                {
                    Console.WriteLine($"[Instance {instanceId}] Initiating clean shutdown via RCON /quit...");
                    await rconService.SendCommandAsync(instanceId, instance.RconPort, instance.RconPassword, "/quit");
                    
                    // Wait for Factorio to cleanly save and exit the container on its own
                    for (int i = 0; i < timeoutSeconds; i++)
                    {
                        try 
                        {
                            ContainerInspectResponse c = await dockerClient.Containers.InspectContainerAsync(containerId);
                            if (!c.State.Running) break;
                        } catch { break; } // Container was removed/stopped
                        
                        await Task.Delay(1000);
                    }
                }

                // Fallback catch-all to ensure the container is stopped if it hung
                await dockerClient.Containers.StopContainerAsync(containerId, new() { WaitBeforeKillSeconds = 10 });
            }
            catch
            {
                // Ignore errors if container already stopped or removed
            }
            finally
            {
                runningContainers.TryRemove(instanceId, out _);
            }
        }
    }

    public async Task DeleteInstanceDataAsync(int instanceId)
    {
        // 1. Stop and remove the container
        string containerName = $"factorio_server_{instanceId}";
        try
        {
            IList<ContainerListResponse> existingContainers = await dockerClient.Containers.ListContainersAsync(new() { All = true });

            foreach (ContainerListResponse? c in existingContainers)
            {
                if (c.Names.Contains($"/{containerName}"))
                {
                    if (c.State == "running")
                        await dockerClient.Containers.StopContainerAsync(c.ID, new() { WaitBeforeKillSeconds = 60 });

                    await dockerClient.Containers.RemoveContainerAsync(c.ID, new() { Force = true });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error removing container during delete: {ex.Message}");
        }
        finally
        {
            runningContainers.TryRemove(instanceId, out _);
        }

        // 2. Delete host directory
        string instanceHostPath = GetInstanceHostPath(instanceId);
        string localDataPath = GetLocalDataPath(instanceId);

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
        return runningContainers.ContainsKey(instanceId);
    }

    public async Task<MultiplexedStream?> GetLogStreamAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        if (runningContainers.TryGetValue(instanceId, out string? containerId))
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

                return await dockerClient.Containers.GetContainerLogsAsync(containerId, false, parameters, cancellationToken);
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
        if (runningContainers.TryGetValue(instanceId, out string? containerId))
        {
            try
            {
                ContainerInspectResponse inspect = await dockerClient.Containers.InspectContainerAsync(containerId);
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
        string instanceHostPath = GetInstanceHostPath(instanceId);

        try
        {
            await dockerClient.Images.CreateImageAsync(new() { FromImage = image }, null, new Progress<JSONMessage>());

            CreateContainerResponse response = await dockerClient.Containers.CreateContainerAsync(new()
            {
                Image = image,
                Name = containerName,
                HostConfig = new()
                {
                    Binds = [$"{instanceHostPath}:/factorio"]
                },
                // Override the default entrypoint script so it doesn't try to boot a multiplayer server
                Entrypoint = ["/opt/factorio/bin/x64/factorio"],
                // Run the sync-mods command and immediately exit
                Cmd = ["--sync-mods", $"/factorio/saves/{saveName}", "--mod-directory", "/factorio/mods"]
            });

            await dockerClient.Containers.StartContainerAsync(response.ID, null);

            // Wait for it to finish parsing and syncing
            await dockerClient.Containers.WaitContainerAsync(response.ID);

            // Fetch the logs to return to the UI (especially if there's a version mismatch error)
            MultiplexedStream logsStream = await dockerClient.Containers.GetContainerLogsAsync(response.ID, false, new()
            {
                ShowStdout = true,
                ShowStderr = true
            });

            (string stdout, string stderr) logs = await logsStream.ReadOutputToEndAsync(default);
            string fullLog = logs.stdout + "\n" + logs.stderr;

            // Remove the temporary container
            await dockerClient.Containers.RemoveContainerAsync(response.ID, new() { Force = true });

            // If it crashed or had an error, it often outputs Error or fails to write
            bool success = !fullLog.Contains("Error", StringComparison.OrdinalIgnoreCase);

            // ALWAYS check global_mods for any missing files after a sync (or during a failure to fix dependencies)
            await ResolveMissingModsFromGlobalAsync(instanceId);
            
            if (!success && retryCount < 5)
            {
                return await SyncModsWithSaveAsync(instanceId, saveName, imageTag, retryCount + 1);
            }

            return (success, fullLog.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task ResolveMissingModsFromGlobalAsync(int instanceId, Action<int, int, string>? progressCallback = null)
    {
        string globalPath = Path.Combine(internalDataPath, "global_mods");
        if (!Directory.Exists(globalPath)) return;

        string targetModsDir = GetModsDirectory(instanceId);
        if (!Directory.Exists(targetModsDir)) Directory.CreateDirectory(targetModsDir);

        string modListPath = Path.Combine(targetModsDir, "mod-list.json");
        if (!File.Exists(modListPath)) return;

        string modListContent = await File.ReadAllTextAsync(modListPath);
        string[] globalZips = Directory.GetFiles(globalPath, "*.zip");

        List<string> matchingZips = new List<string>();
        foreach (string zip in globalZips)
        {
            string zipName = Path.GetFileName(zip);
            int lastUnderscore = zipName.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                string modName = zipName.Substring(0, lastUnderscore);
                if (modListContent.Contains($"\"{modName}\""))
                {
                    matchingZips.Add(zip);
                }
            }
        }

        int total = matchingZips.Count;
        int current = 0;

        foreach (string zip in matchingZips)
        {
            current++;
            string zipName = Path.GetFileName(zip);
            string targetFile = Path.Combine(targetModsDir, zipName);

            progressCallback?.Invoke(current, total, zipName);

            if (!File.Exists(targetFile))
            {
                try { File.Copy(zip, targetFile, true); } catch {}
            }
            
            if (progressCallback != null && current % 3 == 0)
            {
                await Task.Delay(1);
            }
        }
    }

    public void FactoryResetConfigs(int instanceId)
    {
        string instanceHostPath = GetInstanceHostPath(instanceId);
        string localDataPath = GetLocalDataPath(instanceId);

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
        if (statsCache.TryGetValue(instance.Id, out (DateTime FetchedAt, ServerStats Stats) cached) && (DateTime.UtcNow - cached.FetchedAt).TotalSeconds < 2.5)
            return cached.Stats;

        ServerStats stats = new() { IsOnline = false };

        if (runningContainers.TryGetValue(instance.Id, out string? containerId))
        {
            stats.IsOnline = true;
            try
            {
                // 1. Get Docker Stats
                ContainerStatsParameters param = new() { Stream = false };
                ContainerStatsResponse? lastStats = null;
                SyncProgress<ContainerStatsResponse> progress = new(msg => lastStats = msg);
                await dockerClient.Containers.GetContainerStatsAsync(containerId, param, progress, CancellationToken.None);

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
                        stats.CpuPercentage = (cpuDelta / systemDelta) * 100.0;
                        stats.OnlineCpus = (int)lastStats.CPUStats.OnlineCPUs;
                    }
                }

                // 2. Get RCON Stats
                if (instance.RconPort > 0 && !string.IsNullOrEmpty(instance.RconPassword))
                    stats.OnlinePlayers = await rconService.GetOnlinePlayersAsync(instance.Id, instance.RconPort, instance.RconPassword);
            }
            catch
            {
                // Ignore transient errors on stats pulling
            }
        }

        statsCache[instance.Id] = (DateTime.UtcNow, stats);
        return stats;
    }

    public string GetSavesDirectory(int instanceId)
    {
        string instanceHostPath = GetInstanceHostPath(instanceId);
        string localDataPath = GetLocalDataPath(instanceId);

        return Path.Combine(localDataPath, "saves");
    }

    public string GetModsDirectory(int instanceId)
    {
        string instanceHostPath = GetInstanceHostPath(instanceId);
        string localDataPath = GetLocalDataPath(instanceId);

        return Path.Combine(localDataPath, "mods");
    }

    public string GetConfigDirectory(int instanceId)
    {
        string localDataPath = GetLocalDataPath(instanceId);
        return Path.Combine(localDataPath, "config");
    }

    public string GetGlobalModsDirectory()
    {
        string globalPath = Path.Combine(internalDataPath, "global_mods");
        if (!Directory.Exists(globalPath)) Directory.CreateDirectory(globalPath);
        return globalPath;
    }

    public string GetAllInstancesDirectory()
    {
        return internalBaseMountPath;
    }
}

public class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
