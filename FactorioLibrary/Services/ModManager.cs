using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using FactorioLibrary.Internal;
using FactorioLibrary.Models;
using Microsoft.Extensions.Configuration;

namespace FactorioLibrary.Services;

public class ModManager(IConfiguration config, GlobalSettingsService settingsService, HttpClient? httpClient = null)
{
    private const string ModPortalApiBase = "https://mods.factorio.com/api/mods";
    private readonly HttpClient _httpClient = httpClient ?? Shared.HttpClient;
    private readonly GlobalSettingsService _settingsService = settingsService;
    private readonly string _hostBaseMountPath = config.GetValue<string>("HOST_BASE_MOUNT_PATH") 
        ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "C:\\FactorioServers" : "/mnt/user/appdata/factorio_manager/servers");

    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private Dictionary<string, ModCacheEntry>? _modCache;

    private async Task LoadCacheAsync()
    {
        if (_modCache != null) return;
        
        string cachePath = Path.Combine(GetGlobalModsPath(), "mod-cache.json");
        if (File.Exists(cachePath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(cachePath);
                _modCache = JsonSerializer.Deserialize<Dictionary<string, ModCacheEntry>>(json) ?? [];
            }
            catch { _modCache = []; }
        }
        else
        {
            _modCache = [];
        }
    }

    private async Task SaveCacheAsync()
    {
        if (_modCache == null) return;
        string cachePath = Path.Combine(GetGlobalModsPath(), "mod-cache.json");
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(_modCache, options);
            await File.WriteAllTextAsync(cachePath, json);
        }
        catch { }
    }

    public async Task<ModInfo?> GetCachedModInfoAsync(string modName, bool forceRefresh = false)
    {
        await _cacheLock.WaitAsync();
        try
        {
            await LoadCacheAsync();
            
            if (!forceRefresh && _modCache!.TryGetValue(modName, out var entry))
            {
                if (DateTime.UtcNow - entry.LastChecked < TimeSpan.FromHours(24))
                {
                    return entry.Info;
                }
            }

            HttpResponseMessage response = await _httpClient.GetAsync($"{ModPortalApiBase}/{Uri.EscapeDataString(modName)}");
            ModInfo? info = null;
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                info = JsonSerializer.Deserialize<ModInfo>(json, options);
            }

            _modCache![modName] = new ModCacheEntry 
            { 
                LastChecked = DateTime.UtcNow, 
                Info = info 
            };
            
            await SaveCacheAsync();
            return info;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private string GetInstanceModsPath(int instanceId)
    {
        string localDataPath = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" 
            ? $"/factorio/{instanceId}" 
            : $"{_hostBaseMountPath.TrimEnd('/', '\\')}/{instanceId}";
        return Path.Combine(localDataPath, "mods");
    }

    public string GetGlobalModsPath()
    {
        string basePath = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" 
            ? "/factorio" 
            : _hostBaseMountPath.TrimEnd('/', '\\');
        string globalPath = Path.Combine(basePath, "global_mods");
        if (!Directory.Exists(globalPath)) Directory.CreateDirectory(globalPath);
        return globalPath;
    }

    public async Task<List<LocalModInfo>> GetLocalModsAsync(int instanceId)
    {
        string modsDir = GetInstanceModsPath(instanceId);
        if (!Directory.Exists(modsDir)) return [];

        List<LocalModInfo> localMods = [];
        string[] zipFiles = Directory.GetFiles(modsDir, "*.zip");

        // Parse mod-list.json to see what is enabled
        HashSet<string> enabledMods = [];
        string modListPath = Path.Combine(modsDir, "mod-list.json");
        if (File.Exists(modListPath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(modListPath);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("mods", out JsonElement modsArray))
                {
                    foreach (JsonElement mod in modsArray.EnumerateArray())
                    {
                        string? name = mod.GetProperty("name").GetString();
                        bool isEnabled = mod.GetProperty("enabled").GetBoolean();
                        if (isEnabled && name != null) enabledMods.Add(name);
                    }
                }
            }
            catch { }
        }

        foreach (string zipPath in zipFiles)
        {
            try
            {
                using ZipArchive archive = ZipFile.OpenRead(zipPath);
                ZipArchiveEntry? infoEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("info.json", StringComparison.OrdinalIgnoreCase));
                if (infoEntry != null)
                {
                    using Stream stream = infoEntry.Open();
                    using StreamReader reader = new(stream);
                    string json = reader.ReadToEnd();
                    using JsonDocument doc = JsonDocument.Parse(json);

                    string? name = doc.RootElement.GetProperty("name").GetString();
                    string? version = doc.RootElement.GetProperty("version").GetString();
                    string? title = doc.RootElement.TryGetProperty("title", out JsonElement titleProp) ? titleProp.GetString() : name;

                    if (name != null)
                    {
                        localMods.Add(new LocalModInfo
                        {
                            Name = name,
                            Title = title ?? name,
                            Version = version ?? "0.0.0",
                            FileName = Path.GetFileName(zipPath),
                            IsEnabled = enabledMods.Contains(name)
                        });
                    }
                }
            }
            catch { }
        }
        return localMods;
    }

    public async Task<(bool success, string error)> UpdateModAsync(int instanceId, string modName, string downloadUrl, string newFileName, string oldFileName)
    {
        GlobalSettings settings = _settingsService.GetSettings();
        if (string.IsNullOrEmpty(settings.FactorioUsername) || string.IsNullOrEmpty(settings.FactorioToken))
            return (false, "Factorio Mod Portal credentials are not configured in Settings.");

        string modsDir = GetInstanceModsPath(instanceId);
        if (!Directory.Exists(modsDir)) Directory.CreateDirectory(modsDir);

        string targetFilePath = Path.Combine(modsDir, newFileName);

        try
        {
            // Optimization: check global mod pool
            string globalModsDir = GetGlobalModsPath();
            string globalModPath = Path.Combine(globalModsDir, newFileName);

            bool copiedLocally = false;
            if (File.Exists(globalModPath))
            {
                try
                {
                    File.Copy(globalModPath, targetFilePath, true);
                    copiedLocally = true;
                }
                catch { }
            }

            if (!copiedLocally)
            {
                string finalUrl = $"https://mods.factorio.com{downloadUrl}?username={Uri.EscapeDataString(settings.FactorioUsername.Trim())}&token={Uri.EscapeDataString(settings.FactorioToken.Trim())}";
                using HttpResponseMessage response = await _httpClient.GetAsync(finalUrl, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                    return (false, $"Mod Portal returned {response.StatusCode} when attempting to download.");

                using FileStream fs = new(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs);
                fs.Close();

                // Cache in global pool
                try
                {
                    File.Copy(targetFilePath, globalModPath, true);
                }
                catch { }
            }
            
            // Delete old file if successful and it's a different file
            if (!string.IsNullOrEmpty(oldFileName) && oldFileName != newFileName)
            {
                string oldFilePath = Path.Combine(modsDir, oldFileName);
                if (File.Exists(oldFilePath))
                    File.Delete(oldFilePath);
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"Download failed: {ex.Message}");
        }
    }

    public void CopyToGlobalPool(string filePath)
    {
        try
        {
            if (!File.Exists(filePath) || !filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return;
            string fileName = Path.GetFileName(filePath);
            string globalPath = Path.Combine(GetGlobalModsPath(), fileName);
            File.Copy(filePath, globalPath, true);
        }
        catch { }
    }

    public List<LocalModInfo> GetGlobalMods()
    {
        string modsDir = GetGlobalModsPath();
        if (!Directory.Exists(modsDir)) return [];

        List<LocalModInfo> globalMods = [];
        string[] zipFiles = Directory.GetFiles(modsDir, "*.zip");

        foreach (string zipPath in zipFiles)
        {
            try
            {
                using ZipArchive archive = ZipFile.OpenRead(zipPath);
                ZipArchiveEntry? infoEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("info.json", StringComparison.OrdinalIgnoreCase));
                if (infoEntry != null)
                {
                    using Stream stream = infoEntry.Open();
                    using StreamReader reader = new(stream);
                    string json = reader.ReadToEnd();
                    using JsonDocument doc = JsonDocument.Parse(json);

                    string? name = doc.RootElement.GetProperty("name").GetString();
                    string? version = doc.RootElement.GetProperty("version").GetString();
                    string? title = doc.RootElement.TryGetProperty("title", out JsonElement titleProp) ? titleProp.GetString() : name;

                    if (name != null)
                    {
                        globalMods.Add(new LocalModInfo
                        {
                            Name = name,
                            Title = title ?? name,
                            Version = version ?? "0.0.0",
                            FileName = Path.GetFileName(zipPath),
                            IsEnabled = false
                        });
                    }
                }
            }
            catch { }
        }
        return globalMods;
    }

    public void DeleteGlobalMod(string fileName)
    {
        try
        {
            string globalPath = Path.Combine(GetGlobalModsPath(), fileName);
            if (File.Exists(globalPath))
            {
                File.Delete(globalPath);
            }
        }
        catch { }
    }

    public void ClearUnusedGlobalMods()
    {
        try
        {
            string allInstancesDir = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" 
                ? "/factorio" 
                : _hostBaseMountPath.TrimEnd('/', '\\');

            HashSet<string> usedFileNames = [];
            if (Directory.Exists(allInstancesDir))
            {
                var otherInstanceModDirs = Directory.GetDirectories(allInstancesDir, "*", SearchOption.TopDirectoryOnly)
                    .Select(d => Path.Combine(d, "mods"))
                    .Where(Directory.Exists);

                foreach (var modDir in otherInstanceModDirs)
                {
                    foreach (var file in Directory.GetFiles(modDir, "*.zip"))
                    {
                        usedFileNames.Add(Path.GetFileName(file));
                    }
                }
            }

            string globalModsDir = GetGlobalModsPath();
            foreach (var file in Directory.GetFiles(globalModsDir, "*.zip"))
            {
                if (!usedFileNames.Contains(Path.GetFileName(file)))
                {
                    File.Delete(file);
                }
            }
        }
        catch { }
    }
}

public class LocalModInfo
{
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}

public class ModInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("downloads_count")]
    public int DownloadsCount { get; set; }
    
    [JsonPropertyName("deprecated")]
    public bool Deprecated { get; set; }

    [JsonPropertyName("releases")]
    public List<ModRelease> Releases { get; set; } = [];
}

public class ModRelease
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;
    
    [JsonPropertyName("info_json")]
    public ModInfoJson InfoJson { get; set; } = new();
}

public class ModInfoJson
{
    [JsonPropertyName("factorio_version")]
    public string FactorioVersion { get; set; } = string.Empty;
}

public class ModCacheEntry
{
    public DateTime LastChecked { get; set; }
    public ModInfo? Info { get; set; }
}
