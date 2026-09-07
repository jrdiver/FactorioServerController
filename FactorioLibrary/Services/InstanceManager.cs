using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FactorioLibrary.Data;
using FactorioLibrary.Models;
using FactorioLibrary.Services.Orchestrators;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactorioLibrary.Services;

public class InstanceManager
{
    private readonly IContainerOrchestrator _orchestrator;
    private readonly string hostBaseMountPath;
    private readonly string internalBaseMountPath;
    private readonly string hostDataPath;
    private readonly string internalDataPath;
    private readonly RconService rconService;
    private readonly IServiceScopeFactory scopeFactory;

    // Tracks instance IDs currently running a backup
    private readonly ConcurrentDictionary<int, bool> activeBackups = new();

    public InstanceManager(IConfiguration configuration, RconService rconService, IServiceScopeFactory scopeFactory, IContainerOrchestrator orchestrator)
    {
        this.rconService = rconService;
        this.scopeFactory = scopeFactory;
        _orchestrator = orchestrator;

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
    }

    public string GetLocalDataPath(int instanceId) => Path.Combine(internalBaseMountPath, instanceId.ToString());
    public string GetInstanceHostPath(int instanceId) => Path.Combine(hostBaseMountPath, instanceId.ToString());
    public string GetBackupsDirectory(int instanceId)
    {
        string path = Path.Combine(internalDataPath, "backups", instanceId.ToString());
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        return path;
    }

    public bool IsBackingUp(int instanceId) => activeBackups.ContainsKey(instanceId);
    public void SetBackingUp(int instanceId, bool isBackingUp)
    {
        if (isBackingUp) activeBackups.TryAdd(instanceId, true);
        else activeBackups.TryRemove(instanceId, out _);
    }

    public async Task<(bool Success, bool CleanedCorruptSave)> StartInstanceAsync(ServerInstance instance)
    {
        try
        {
            string localDataPath = GetLocalDataPath(instance.Id);

            // Pre-create all folders so we can set proper permissions on them
            Directory.CreateDirectory(localDataPath);
            Directory.CreateDirectory(Path.Combine(localDataPath, "config"));
            Directory.CreateDirectory(Path.Combine(localDataPath, "saves"));
            Directory.CreateDirectory(Path.Combine(localDataPath, "mods"));
            
            await File.WriteAllTextAsync(Path.Combine(localDataPath, "config", "rconpw"), instance.RconPassword);

            // ARM builds of factoriotools drop privileges to uid 845 immediately, 
            // preventing them from reading the rconpw or writing saves if created by root.
            // We run chmod 777 so both the C# app and the factorio user can read/write smoothly.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"-R 777 \"{localDataPath}\"",
                    UseShellExecute = false
                })?.WaitForExit();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not pre-create rconpw file or fix permissions: {ex.Message}");
        }

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

        bool loadLatest = false;
        bool generateNewSave = false;
        
        if (string.IsNullOrEmpty(instance.ActiveSaveName))
        {
            if (hasSaves)
            {
                loadLatest = true;
            }
            else
            {
                instance.ActiveSaveName = instance.Name + ".zip"; // just for this startup logic
                generateNewSave = true;
            }
        }
        else if (!hasSaves || !File.Exists(Path.Combine(savesDir, instance.ActiveSaveName)))
        {
            // If they have an ActiveSaveName specified but the file itself doesn't actually exist, generate it
            generateNewSave = true;
        }

        bool started = await _orchestrator.StartContainerAsync(instance, hostBaseMountPath, GetLocalDataPath(instance.Id), GetSavesDirectory(instance.Id), loadLatest, generateNewSave);

        return (started, cleanedCorruptSave);
    }

    public async Task StopInstanceAsync(int instanceId)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        ServerInstance? instance = await dbContext.ServerInstances.FindAsync(instanceId);
        await _orchestrator.StopContainerAsync(instanceId, instance);
    }

    public async Task DeleteInstanceDataAsync(int instanceId)
    {
        await _orchestrator.RemoveContainerAsync(instanceId);

        // 2. Delete host directory
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
        return _orchestrator.IsContainerRunning(instanceId);
    }

    public IAsyncEnumerable<string> GetLiveLogsAsync(int instanceId, CancellationToken cancellationToken) => _orchestrator.GetLiveLogsAsync(instanceId, cancellationToken);

    public async Task<List<string>> GetOfflineLogsAsync(int instanceId, int tail = 200)
    {
        return await _orchestrator.GetOfflineLogsAsync(instanceId, tail);
    }

    public async Task<string?> GetContainerIpAddressAsync(int instanceId)
    {
        return await _orchestrator.GetContainerIpAddressAsync(instanceId);
    }

    public async Task<(bool Success, string Logs)> SyncModsWithSaveAsync(int instanceId, string saveName, string imageTag, int retryCount = 0)
    {
        (bool success, string logs) = await _orchestrator.RunTemporarySyncContainerAsync(instanceId, saveName, imageTag, GetInstanceHostPath(instanceId));

        // ALWAYS check global_mods for any missing files after a sync (or during a failure to fix dependencies)
        using IServiceScope scope = scopeFactory.CreateScope();
        ModManager modManager = scope.ServiceProvider.GetRequiredService<ModManager>();
        List<LocalModInfo> globalMods = modManager.GetGlobalMods();
        await ResolveMissingModsFromGlobalAsync(instanceId, globalMods);
        
        if (!success && retryCount < 5)
        {
            return await SyncModsWithSaveAsync(instanceId, saveName, imageTag, retryCount + 1);
        }

        return (success, logs);
    }

    public async Task ResolveMissingModsFromGlobalAsync(int instanceId, IEnumerable<LocalModInfo> globalMods, Action<int, int, string, long>? progressCallback = null)
    {
        string targetModsDir = GetModsDirectory(instanceId);
        if (!Directory.Exists(targetModsDir)) Directory.CreateDirectory(targetModsDir);

        string modListPath = Path.Combine(targetModsDir, "mod-list.json");
        if (!File.Exists(modListPath)) return;

        string modListContent = await File.ReadAllTextAsync(modListPath);
        
        List<LocalModInfo> matchingMods = globalMods.Where(m => modListContent.Contains($"\"{m.Name}\"")).ToList();

        int total = matchingMods.Count;
        int current = 0;

        string globalPath = Path.Combine(internalDataPath, "global_mods");

        foreach (LocalModInfo mod in matchingMods)
        {
            current++;
            string targetFile = Path.Combine(targetModsDir, mod.FileName);

            progressCallback?.Invoke(current, total, mod.FileName, mod.SizeBytes);

            if (!File.Exists(targetFile))
            {
                try 
                { 
                    string globalFile = Path.Combine(globalPath, mod.FileName);
                    File.Copy(globalFile, targetFile, true); 
                } 
                catch {}
            }
            
            if (progressCallback != null && current % 3 == 0)
            {
                await Task.Delay(1);
            }
        }
    }

    public void FactoryResetConfigs(int instanceId)
    {
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
        return await _orchestrator.GetContainerStatsAsync(instance);
    }

    public string GetSavesDirectory(int instanceId)
    {
        string localDataPath = GetLocalDataPath(instanceId);

        return Path.Combine(localDataPath, "saves");
    }

    public string GetModsDirectory(int instanceId)
    {
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
