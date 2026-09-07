using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using FactorioLibrary.Models;

namespace FactorioLibrary.Services.Orchestrators;

public class DockerOrchestrator : IContainerOrchestrator
{
    private readonly DockerClient dockerClient;
    private readonly RconService rconService;

    // Track running containers by instance ID
    private readonly Dictionary<int, string> runningContainers = [];
    private readonly Timer syncTimer;

    public string OrchestratorName => "Docker";

    public DockerOrchestrator(RconService rconService)
    {
        this.rconService = rconService;
        
        string dockerUri = "unix:///var/run/docker.sock";
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            dockerUri = "npipe://./pipe/docker_engine";
        }
        else if (Environment.GetEnvironmentVariable("DOCKER_HOST") != null)
        {
            dockerUri = Environment.GetEnvironmentVariable("DOCKER_HOST")!;
        }

        dockerClient = new DockerClientConfiguration(new Uri(dockerUri)).CreateClient();
        
        syncTimer = new Timer(async _ => await SyncRunningContainersAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    public async Task<(bool IsHealthy, string Message)> CheckHealthAsync()
    {
        try
        {
            await dockerClient.System.PingAsync();
            await SyncRunningContainersAsync();
            return (true, "Connected to Docker Daemon.");
        }
        catch (Exception ex)
        {
            return (false, $"Docker socket unreachable: {ex.Message}");
        }
    }

    private async Task SyncRunningContainersAsync()
    {
        try
        {
            IList<ContainerListResponse> containers = await dockerClient.Containers.ListContainersAsync(new() { All = false });
            HashSet<int> activeIds = new();
            foreach (ContainerListResponse c in containers)
            {
                string name = c.Names.FirstOrDefault(n => n.StartsWith("/factorio_server_")) ?? "";
                if (name.Length > 0 && int.TryParse(name.Replace("/factorio_server_", ""), out int id))
                {
                    runningContainers[id] = c.ID;
                    activeIds.Add(id);
                }
            }
            
            foreach (int key in runningContainers.Keys.ToList())
            {
                if (!activeIds.Contains(key)) runningContainers.Remove(key);
            }
        }
        catch { }
    }

    public bool IsContainerRunning(int instanceId)
    {
        return runningContainers.ContainsKey(instanceId);
    }

    public async Task<bool> StartContainerAsync(ServerInstance instance, string hostBaseMountPath, string localDataPath, string savesDir, bool loadLatest, bool generateNewSave)
    {
        string image = string.IsNullOrWhiteSpace(instance.AssignedVersion) ? "factoriotools/factorio:latest" : $"factoriotools/factorio:{instance.AssignedVersion}";
        string containerName = $"factorio_server_{instance.Id}";

        try
        {
            Console.WriteLine($"[Instance {instance.Id}] Ensuring image is pulled...");
            await dockerClient.Images.CreateImageAsync(new() { FromImage = image }, null, new Progress<JSONMessage>());

            Console.WriteLine($"[Instance {instance.Id}] Checking for existing containers named {containerName}...");
            IList<ContainerListResponse> existingContainers = await dockerClient.Containers.ListContainersAsync(new() { All = true });
            foreach (ContainerListResponse c in existingContainers)
            {
                if (c.Names.Contains($"/{containerName}"))
                {
                    Console.WriteLine($"[Instance {instance.Id}] Found stopped container {c.ID}. Removing it...");
                    try { await dockerClient.Containers.RemoveContainerAsync(c.ID, new() { Force = true }); } catch { }
                }
            }

            string instanceHostPath = $"{hostBaseMountPath.TrimEnd('/', '\\')}/{instance.Id}";

            List<string> envVars =
            [
                $"PORT=34197",
                $"RCON_PORT=27015",
                $"RCON_PASSWORD={instance.RconPassword}",
                $"LOAD_LATEST_SAVE={loadLatest.ToString().ToLower()}",
                $"GENERATE_NEW_SAVE={generateNewSave.ToString().ToLower()}"
            ];

            if (!string.IsNullOrEmpty(instance.ActiveSaveName) && !loadLatest && !generateNewSave)
            {
                string saveNameWithoutExtension = instance.ActiveSaveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? instance.ActiveSaveName.Substring(0, instance.ActiveSaveName.Length - 4) : instance.ActiveSaveName;
                envVars.Add($"SAVE_NAME={saveNameWithoutExtension}");
            }

            Console.WriteLine($"[Instance {instance.Id}] Creating Docker container {containerName} with Port: {instance.Port}, RconPort: {instance.RconPort}...");

            CreateContainerResponse response = await dockerClient.Containers.CreateContainerAsync(new()
            {
                Image = image,
                Name = containerName,
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
                },
                Env = envVars
            });

            Console.WriteLine($"[Instance {instance.Id}] Starting Docker container {response.ID}...");
            bool started = await dockerClient.Containers.StartContainerAsync(response.ID, null);
            
            if (started)
            {
                runningContainers[instance.Id] = response.ID;
                Console.WriteLine($"[Instance {instance.Id}] Successfully started!");
            }
            
            return started;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Instance {instance.Id}] Error starting container: {ex.Message}");
            return false;
        }
    }

    public async Task StopContainerAsync(int instanceId, ServerInstance? instance)
    {
        try
        {
            if (runningContainers.TryGetValue(instanceId, out string? containerId))
            {
                Console.WriteLine($"[Instance {instanceId}] Stopping container {containerId}...");
                
                if (instance != null)
                {
                    Console.WriteLine($"[Instance {instanceId}] Initiating clean shutdown via RCON /quit...");
                    await rconService.SendCommandAsync(instanceId, instance.RconPort, instance.RconPassword, "/quit");
                    
                    for (int i = 0; i < 30; i++)
                    {
                        try 
                        {
                            ContainerInspectResponse c = await dockerClient.Containers.InspectContainerAsync(containerId);
                            if (!c.State.Running) break;
                        } 
                        catch { break; }
                        await Task.Delay(1000);
                    }
                }

                try
                {
                    await dockerClient.Containers.StopContainerAsync(containerId, new() { WaitBeforeKillSeconds = 10 });
                }
                catch { }

                runningContainers.Remove(instanceId);
            }
            else
            {
                string containerName = $"factorio_server_{instanceId}";
                IList<ContainerListResponse> existingContainers = await dockerClient.Containers.ListContainersAsync(new() { All = true });
                foreach (ContainerListResponse c in existingContainers)
                {
                    if (c.Names.Contains($"/{containerName}"))
                    {
                        if (c.State == "running")
                        {
                            await dockerClient.Containers.StopContainerAsync(c.ID, new() { WaitBeforeKillSeconds = 60 });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Instance {instanceId}] Error stopping container: {ex.Message}");
        }
    }

    public async Task RemoveContainerAsync(int instanceId)
    {
        try
        {
            string containerName = $"factorio_server_{instanceId}";
            IList<ContainerListResponse> existingContainers = await dockerClient.Containers.ListContainersAsync(new() { All = true });
            foreach (ContainerListResponse c in existingContainers)
            {
                if (c.Names.Contains($"/{containerName}"))
                {
                    await dockerClient.Containers.RemoveContainerAsync(c.ID, new() { Force = true });
                }
            }
            runningContainers.Remove(instanceId);
        }
        catch { }
    }

    public async Task<ServerStats> GetContainerStatsAsync(ServerInstance instance)
    {
        ServerStats stats = new() { IsOnline = false };

        if (runningContainers.TryGetValue(instance.Id, out string? containerId))
        {
            stats.IsOnline = true;
            try
            {
                ContainerStatsParameters param = new() { Stream = false };
                ContainerStatsResponse? lastStats = null;
                SyncProgress<ContainerStatsResponse> progress = new(msg => lastStats = msg);
                await dockerClient.Containers.GetContainerStatsAsync(containerId, param, progress, CancellationToken.None);

                if (lastStats != null)
                {
                    stats.RamUsageMb = lastStats.MemoryStats.Usage / (1024 * 1024.0);
                    stats.RamLimitMb = lastStats.MemoryStats.Limit / (1024 * 1024.0);

                    double cpuDelta = lastStats.CPUStats.CPUUsage.TotalUsage - lastStats.PreCPUStats.CPUUsage.TotalUsage;
                    double systemDelta = lastStats.CPUStats.SystemUsage - lastStats.PreCPUStats.SystemUsage;
                                          
                    if (systemDelta > 0.0 && cpuDelta > 0.0)
                    {
                        stats.CpuPercentage = (cpuDelta / systemDelta) * 100.0;
                        stats.OnlineCpus = (int)lastStats.CPUStats.OnlineCPUs;
                    }
                }

                if (instance.RconPort > 0 && !string.IsNullOrEmpty(instance.RconPassword))
                    stats.OnlinePlayers = await rconService.GetOnlinePlayersAsync(instance.Id, instance.RconPort, instance.RconPassword);
            }
            catch { }
        }
        return stats;
    }

    public async Task<List<string>> GetOfflineLogsAsync(int instanceId, int tail = 200)
    {
        List<string> logs = [];
        try
        {
            string containerName = $"factorio_server_{instanceId}";
            var containers = await dockerClient.Containers.ListContainersAsync(new() { All = true });
            var container = containers.FirstOrDefault(c => c.Names.Contains($"/{containerName}"));
            
            if (container != null)
            {
                using var stream = await dockerClient.Containers.GetContainerLogsAsync(container.ID, false, new()
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Tail = tail.ToString()
                });
                
                (string stdout, string stderr) output = await stream.ReadOutputToEndAsync(default);
                string combined = output.stdout + "\n" + output.stderr;
                
                string[] lines = combined.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                logs.AddRange(lines.TakeLast(tail));
            }
        }
        catch { }
        
        return logs;
    }

    public async IAsyncEnumerable<string> GetLiveLogsAsync(int instanceId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (runningContainers.TryGetValue(instanceId, out string? containerId))
        {
            MultiplexedStream? stream = null;
            try
            {
                stream = await dockerClient.Containers.GetContainerLogsAsync(containerId, false, new()
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Follow = true,
                    Tail = "100"
                }, cancellationToken);

                byte[] buffer = new byte[81920];
                while (!cancellationToken.IsCancellationRequested)
                {
                    MultiplexedStream.ReadResult result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (result.EOF) break;
                    
                    string text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        yield return line.TrimEnd('\r');
                    }
                }
            }
            finally
            {
                stream?.Dispose();
            }
        }
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

    public async Task<(bool Success, string Logs)> RunTemporarySyncContainerAsync(int instanceId, string saveName, string imageTag, string instanceHostPath)
    {
        string image = string.IsNullOrWhiteSpace(imageTag) ? "factoriotools/factorio:latest" : $"factoriotools/factorio:{imageTag}";
        string containerName = $"factorio_sync_{instanceId}_{Guid.NewGuid().ToString()[..8]}";

        try
        {
            await dockerClient.Images.CreateImageAsync(new() { FromImage = image }, null, new Progress<JSONMessage>());

            CreateContainerResponse response = await dockerClient.Containers.CreateContainerAsync(new()
            {
                Image = image,
                Name = containerName,
                HostConfig = new() { Binds = [$"{instanceHostPath}:/factorio"] },
                Entrypoint = ["/opt/factorio/bin/x64/factorio"],
                Cmd = ["--sync-mods", $"/factorio/saves/{saveName}", "--mod-directory", "/factorio/mods"]
            });

            await dockerClient.Containers.StartContainerAsync(response.ID, null);
            await dockerClient.Containers.WaitContainerAsync(response.ID);

            MultiplexedStream logsStream = await dockerClient.Containers.GetContainerLogsAsync(response.ID, false, new() { ShowStdout = true, ShowStderr = true });
            (string stdout, string stderr) logs = await logsStream.ReadOutputToEndAsync(default);
            string fullLog = logs.stdout + "\n" + logs.stderr;

            await dockerClient.Containers.RemoveContainerAsync(response.ID, new() { Force = true });
            bool success = !fullLog.Contains("Error", StringComparison.OrdinalIgnoreCase);

            return (success, fullLog.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

public class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
