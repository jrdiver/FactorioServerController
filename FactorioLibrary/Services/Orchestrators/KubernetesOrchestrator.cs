using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FactorioLibrary.Models;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace FactorioLibrary.Services.Orchestrators;

public class KubernetesOrchestrator : IContainerOrchestrator
{
    private readonly IKubernetes _client;
    private readonly RconService _rconService;
    private readonly string _namespace = "default";
    private readonly Dictionary<int, string> _runningPods = [];
    private readonly Dictionary<int, string> _podHosts = [];
    private readonly Timer _syncTimer;

    public string OrchestratorName => "Kubernetes";

    public KubernetesOrchestrator(RconService rconService)
    {
        _rconService = rconService;
        KubernetesClientConfiguration config = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        _client = new Kubernetes(config);
        _syncTimer = new(async _ => await SyncRunningPodsAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    public async Task<(bool IsHealthy, string Message)> CheckHealthAsync()
    {
        try
        {
            await SyncRunningPodsAsync();
            try
            {
                VersionInfo version = await _client.Version.GetCodeAsync();
                return (true, $"Connected to Kubernetes API v{version.GitVersion}");
            }
            catch
            {
                await _client.CoreV1.ListNamespacedPodAsync(_namespace, limit: 1);
                return (true, "Connected to Kubernetes API.");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Kubernetes API unreachable: {ex.Message}");
        }
    }

    private async Task SyncRunningPodsAsync()
    {
        try
        {
            V1PodList pods = await _client.CoreV1.ListNamespacedPodAsync(_namespace);
            HashSet<int> activeIds = [];
            foreach (V1Pod pod in pods.Items)
            {
                string name = pod.Metadata?.Name ?? "";
                if (!name.StartsWith("factorio-server-") || pod.Metadata?.DeletionTimestamp != null)
                    continue;

                string? phase = pod.Status?.Phase;
                if ((phase == "Running" || phase == "Pending") && int.TryParse(name.Replace("factorio-server-", ""), out int id))
                {
                    _runningPods[id] = name;
                    string? hostIp = pod.Status?.HostIP;
                    string? nodeName = pod.Spec?.NodeName;
                    if (!string.IsNullOrEmpty(hostIp))
                        _podHosts[id] = !string.IsNullOrEmpty(nodeName) ? $"{hostIp} ({nodeName})" : hostIp;
                    else if (!string.IsNullOrEmpty(nodeName))
                        _podHosts[id] = nodeName;
                    activeIds.Add(id);
                }
            }

            foreach (int key in _runningPods.Keys.ToList())
            {
                if (!activeIds.Contains(key))
                {
                    _runningPods.Remove(key);
                    _podHosts.Remove(key);
                }
            }
        }
        catch { }
    }

    public bool IsContainerRunning(int instanceId) => _runningPods.ContainsKey(instanceId);

    public string? GetContainerHostInfo(int instanceId) => _podHosts.GetValueOrDefault(instanceId);

    private static string? ReadBase64FileIfExists(string path) => File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null;

    public async Task<bool> StartContainerAsync(ServerInstance instance, string hostBaseMountPath, string localDataPath, string savesDir, bool loadLatest, bool generateNewSave)
    {
        string name = $"factorio-server-{instance.Id}";
        string pvcName = $"factorio-pvc-{instance.Id}";
        string svcName = $"factorio-svc-{instance.Id}";

        try
        {
            Console.WriteLine($"[Kubernetes] Starting instance {instance.Id} ({name})...");

            try
            {
                await _client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(pvcName, _namespace);
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                V1PersistentVolumeClaim pvc = new()
                {
                    Metadata = new() { Name = pvcName },
                    Spec = new()
                    {
                        AccessModes = ["ReadWriteOnce"],
                        Resources = new()
                        {
                            Requests = new Dictionary<string, ResourceQuantity> { { "storage", new("10Gi") } }
                        }
                    }
                };
                await _client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(pvc, _namespace);
            }

            bool createSvc = false;
            try
            {
                V1Service existingSvc = await _client.CoreV1.ReadNamespacedServiceAsync(svcName, _namespace);
                bool portsMatch = existingSvc.Spec?.Ports != null
                    && existingSvc.Spec.Ports.Any(p => p.Name == "game" && p.Port == instance.Port)
                    && existingSvc.Spec.Ports.Any(p => p.Name == "rcon" && p.Port == instance.RconPort);
                if (!portsMatch)
                {
                    await _client.CoreV1.DeleteNamespacedServiceAsync(svcName, _namespace);
                    createSvc = true;
                }
            }
            catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                createSvc = true;
            }

            if (createSvc)
            {
                V1Service svc = new()
                {
                    Metadata = new() { Name = svcName },
                    Spec = new()
                    {
                        Type = "LoadBalancer",
                        ExternalTrafficPolicy = "Local",
                        Ports =
                        [
                            new() { Name = "game", Port = instance.Port, Protocol = "UDP", TargetPort = 34197 },
                            new() { Name = "rcon", Port = instance.RconPort, Protocol = "TCP", TargetPort = 27015 }
                        ],
                        Selector = new Dictionary<string, string> { { "app", name } }
                    }
                };
                await _client.CoreV1.CreateNamespacedServiceAsync(svc, _namespace);
            }

            string image = string.IsNullOrWhiteSpace(instance.AssignedVersion) ? "factoriotools/factorio:latest" : $"factoriotools/factorio:{instance.AssignedVersion}";

            List<V1EnvVar> envVars =
            [
                new() { Name = "PORT", Value = "34197" },
                new() { Name = "RCON_PORT", Value = "27015" },
                new() { Name = "RCON_PASSWORD", Value = instance.RconPassword },
                new() { Name = "LOAD_LATEST_SAVE", Value = loadLatest.ToString().ToLower() },
                new() { Name = "GENERATE_NEW_SAVE", Value = generateNewSave.ToString().ToLower() }
            ];

            if (!string.IsNullOrEmpty(instance.ActiveSaveName) && !loadLatest)
            {
                string saveNameWithoutExtension = instance.ActiveSaveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? instance.ActiveSaveName[..^4] : instance.ActiveSaveName;
                envVars.Add(new() { Name = "SAVE_NAME", Value = saveNameWithoutExtension });
            }

            string configDir = Path.Combine(localDataPath, "config");
            string? serverSettingsB64 = ReadBase64FileIfExists(Path.Combine(configDir, "server-settings.json"));
            string? adminListB64 = ReadBase64FileIfExists(Path.Combine(configDir, "server-adminlist.json"));
            string? whiteListB64 = ReadBase64FileIfExists(Path.Combine(configDir, "server-whitelist.json"));
            string? banListB64 = ReadBase64FileIfExists(Path.Combine(configDir, "server-banlist.json"));
            string? modListB64 = ReadBase64FileIfExists(Path.Combine(localDataPath, "mods", "mod-list.json"));

            if (serverSettingsB64 != null) envVars.Add(new() { Name = "CFG_SERVER_SETTINGS", Value = serverSettingsB64 });
            if (adminListB64 != null) envVars.Add(new() { Name = "CFG_ADMINLIST", Value = adminListB64 });
            if (whiteListB64 != null) envVars.Add(new() { Name = "CFG_WHITELIST", Value = whiteListB64 });
            if (banListB64 != null) envVars.Add(new() { Name = "CFG_BANLIST", Value = banListB64 });
            if (modListB64 != null) envVars.Add(new() { Name = "CFG_MODLIST", Value = modListB64 });

            string startupScript =
                "mkdir -p /factorio/config /factorio/saves /factorio/mods && " +
                "rm -f /factorio/saves/*.tmp.zip && " +
                "printf '%s' \"$RCON_PASSWORD\" > /factorio/config/rconpw && " +
                "if [ -n \"$CFG_SERVER_SETTINGS\" ]; then printf '%s' \"$CFG_SERVER_SETTINGS\" | base64 -d > /factorio/config/server-settings.json; fi && " +
                "if [ -n \"$CFG_ADMINLIST\" ]; then printf '%s' \"$CFG_ADMINLIST\" | base64 -d > /factorio/config/server-adminlist.json; fi && " +
                "if [ -n \"$CFG_WHITELIST\" ]; then printf '%s' \"$CFG_WHITELIST\" | base64 -d > /factorio/config/server-whitelist.json; fi && " +
                "if [ -n \"$CFG_BANLIST\" ]; then printf '%s' \"$CFG_BANLIST\" | base64 -d > /factorio/config/server-banlist.json; fi && " +
                "if [ -n \"$CFG_MODLIST\" ]; then printf '%s' \"$CFG_MODLIST\" | base64 -d > /factorio/mods/mod-list.json; fi && " +
                "if [ -n \"$SAVE_NAME\" ] && [ -f \"/factorio/saves/${SAVE_NAME}.zip\" ]; then export GENERATE_NEW_SAVE=false; " +
                "elif [ \"$(find -L /factorio/saves -iname '*.zip' -mindepth 1 | wc -l)\" -gt 0 ] && [ \"$GENERATE_NEW_SAVE\" = \"true\" ]; then export GENERATE_NEW_SAVE=false; export LOAD_LATEST_SAVE=true; fi && " +
                "exec /docker-entrypoint.sh";

            V1Pod pod = new()
            {
                Metadata = new()
                {
                    Name = name,
                    Labels = new Dictionary<string, string> { { "app", name } }
                },
                Spec = new()
                {
                    RestartPolicy = "OnFailure",
                    Containers =
                    [
                        new()
                        {
                            Name = "factorio",
                            Image = image,
                            Command = ["/bin/sh", "-c", startupScript],
                            Ports =
                            [
                                new() { ContainerPort = 34197, Protocol = "UDP" },
                                new() { ContainerPort = 27015, Protocol = "TCP" }
                            ],
                            Env = envVars,
                            VolumeMounts =
                            [
                                new() { Name = "data", MountPath = "/factorio" }
                            ]
                        }
                    ],
                    Volumes =
                    [
                        new() { Name = "data", PersistentVolumeClaim = new() { ClaimName = pvcName } }
                    ]
                }
            };

            if (!string.Equals(instance.TargetArchitecture, "Any", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(instance.TargetArchitecture))
                pod.Spec.NodeSelector = new Dictionary<string, string> { { "kubernetes.io/arch", instance.TargetArchitecture.ToLower() } };

            try
            {
                await _client.CoreV1.DeleteNamespacedPodAsync(name, _namespace);
                for (int i = 0; i < 30; i++)
                {
                    try
                    {
                        await _client.CoreV1.ReadNamespacedPodAsync(name, _namespace);
                        await Task.Delay(1000);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            catch { }

            await _client.CoreV1.CreateNamespacedPodAsync(pod, _namespace);
            _runningPods[instance.Id] = name;
            Console.WriteLine($"[Kubernetes] Pod {name} created successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Kubernetes] Error starting pod {name}: {ex.Message}");
            return false;
        }
    }

    public async Task StopContainerAsync(int instanceId, ServerInstance? instance)
    {
        string name = $"factorio-server-{instanceId}";
        string svcName = $"factorio-svc-{instanceId}";
        if (instance != null)
        {
            try
            {
                await _rconService.SendCommandAsync(instanceId, instance.RconPort, instance.RconPassword, "/quit");
            }
            catch { }
            await Task.Delay(2000);
        }
        try
        {
            await _client.CoreV1.DeleteNamespacedPodAsync(name, _namespace);
        }
        catch { }
        try
        {
            await _client.CoreV1.DeleteNamespacedServiceAsync(svcName, _namespace);
        }
        catch { }
        _runningPods.Remove(instanceId);
        _podHosts.Remove(instanceId);
    }

    public async Task RemoveContainerAsync(int instanceId)
    {
        string name = $"factorio-server-{instanceId}";
        string pvcName = $"factorio-pvc-{instanceId}";
        string svcName = $"factorio-svc-{instanceId}";

        try { await _client.CoreV1.DeleteNamespacedPodAsync(name, _namespace); } catch { }
        try { await _client.CoreV1.DeleteNamespacedServiceAsync(svcName, _namespace); } catch { }
        try { await _client.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(pvcName, _namespace); } catch { }
        _runningPods.Remove(instanceId);
        _podHosts.Remove(instanceId);
    }

    public async Task<ServerStats> GetContainerStatsAsync(ServerInstance instance)
    {
        ServerStats stats = new() { IsOnline = false };
        string name = $"factorio-server-{instance.Id}";
        try
        {
            V1Pod pod = await _client.CoreV1.ReadNamespacedPodAsync(name, _namespace);
            string? phase = pod.Status?.Phase;
            if (pod.Metadata?.DeletionTimestamp == null && (phase == "Running" || phase == "Pending"))
            {
                stats.IsOnline = true;
                stats.HostIp = pod.Status?.HostIP;
                stats.NodeName = pod.Spec?.NodeName;
                _runningPods[instance.Id] = name;

                if (!string.IsNullOrEmpty(stats.HostIp))
                    _podHosts[instance.Id] = !string.IsNullOrEmpty(stats.NodeName) ? $"{stats.HostIp} ({stats.NodeName})" : stats.HostIp;

                if (phase == "Running")
                {
                    try
                    {
                        PodMetricsList metricsList = await _client.GetKubernetesPodsMetricsByNamespaceAsync(_namespace);
                        PodMetrics? podMetrics = metricsList.Items?.FirstOrDefault(m => m.Metadata?.Name == name);
                        ContainerMetrics? containerMetrics = podMetrics?.Containers?.FirstOrDefault(c => c.Name == "factorio") ?? podMetrics?.Containers?.FirstOrDefault();
                        if (containerMetrics?.Usage != null)
                        {
                            int cpus = Math.Max(1, Environment.ProcessorCount);
                            stats.OnlineCpus = cpus;
                            if (containerMetrics.Usage.TryGetValue("cpu", out ResourceQuantity? cpuQty))
                                stats.CpuPercentage = (cpuQty.ToDouble() / cpus) * 100.0;
                            if (containerMetrics.Usage.TryGetValue("memory", out ResourceQuantity? memQty))
                            {
                                stats.RamUsageMb = memQty.ToDouble() / (1024.0 * 1024.0);
                                stats.RamLimitMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Kubernetes] Metrics query failed for {name}: {ex.Message}");
                    }

                    if (instance.RconPort > 0 && !string.IsNullOrEmpty(instance.RconPassword))
                        stats.OnlinePlayers = await _rconService.GetOnlinePlayersAsync(instance.Id, instance.RconPort, instance.RconPassword);
                }
            }
            else
            {
                _runningPods.Remove(instance.Id);
                _podHosts.Remove(instance.Id);
            }
        }
        catch
        {
            _runningPods.Remove(instance.Id);
            _podHosts.Remove(instance.Id);
        }
        return stats;
    }

    public async Task<List<string>> GetOfflineLogsAsync(int instanceId, int tail = 200)
    {
        string name = $"factorio-server-{instanceId}";
        try
        {
            using Stream stream = await _client.CoreV1.ReadNamespacedPodLogAsync(name, _namespace, tailLines: tail);
            using StreamReader reader = new(stream);
            string all = await reader.ReadToEndAsync();
            return all.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch
        {
            return ["[No logs available]"];
        }
    }

    public async IAsyncEnumerable<string> GetLiveLogsAsync(int instanceId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsContainerRunning(instanceId))
            yield break;

        string name = $"factorio-server-{instanceId}";
        Stream? stream = null;
        try
        {
            for (int attempt = 0; attempt < 30 && !cancellationToken.IsCancellationRequested && IsContainerRunning(instanceId); attempt++)
            {
                try
                {
                    stream = await _client.CoreV1.ReadNamespacedPodLogAsync(name, _namespace, follow: true, tailLines: 100, cancellationToken: cancellationToken);
                    break;
                }
                catch
                {
                }

                await Task.Delay(2000, cancellationToken);
            }

            if (stream == null)
                yield break;

            using StreamReader reader = new(stream);
            string? line;
            while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync(cancellationToken)) != null)
                yield return line;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public async Task<string?> GetContainerIpAddressAsync(int instanceId)
    {
        string name = $"factorio-server-{instanceId}";
        try
        {
            V1Pod pod = await _client.CoreV1.ReadNamespacedPodAsync(name, _namespace);
            return pod.Status?.PodIP;
        }
        catch
        {
            return null;
        }
    }

    public async Task<(bool Success, string Logs)> RunTemporarySyncContainerAsync(int instanceId, string saveName, string imageTag, string instanceHostPath)
    {
        string podName = $"factorio-sync-{instanceId}-{Guid.NewGuid().ToString()[..5]}";
        string image = string.IsNullOrWhiteSpace(imageTag) ? "factoriotools/factorio:latest" : $"factoriotools/factorio:{imageTag}";

        V1Pod pod = new()
        {
            Metadata = new() { Name = podName },
            Spec = new()
            {
                RestartPolicy = "Never",
                Containers =
                [
                    new()
                    {
                        Name = "sync",
                        Image = image,
                        Command = ["/opt/factorio/bin/x64/factorio", "--sync-mods", $"/factorio/saves/{saveName}", "--mod-directory", "/factorio/mods"],
                        VolumeMounts = [new() { Name = "data", MountPath = "/factorio" }]
                    }
                ],
                Volumes = [new() { Name = "data", PersistentVolumeClaim = new() { ClaimName = $"factorio-pvc-{instanceId}" } }]
            }
        };

        try
        {
            await _client.CoreV1.CreateNamespacedPodAsync(pod, _namespace);

            string logs = "";
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(2000);
                V1Pod p = await _client.CoreV1.ReadNamespacedPodAsync(podName, _namespace);
                if (p.Status?.Phase == "Succeeded" || p.Status?.Phase == "Failed")
                {
                    using Stream stream = await _client.CoreV1.ReadNamespacedPodLogAsync(podName, _namespace);
                    using StreamReader reader = new(stream);
                    logs = await reader.ReadToEndAsync();
                    break;
                }
            }

            await _client.CoreV1.DeleteNamespacedPodAsync(podName, _namespace);
            return (!logs.Contains("Error", StringComparison.OrdinalIgnoreCase), logs.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
