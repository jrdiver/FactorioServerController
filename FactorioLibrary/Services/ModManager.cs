using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using FactorioLibrary.Internal;
using FactorioLibrary.Models;

namespace FactorioLibrary.Services;

public class ModManager(GlobalSettingsService settingsService, InstanceManager instanceManager, HttpClient? httpClient = null)
{
    private const string ModPortalApiBase = "https://mods.factorio.com/api/mods";
    private readonly HttpClient httpClient = httpClient ?? Shared.HttpClient;

    private readonly SemaphoreSlim cacheLock = new(1, 1);
    private Dictionary<string, ModCacheEntry>? modCache;

    private async Task LoadCacheAsync()
    {
        if (modCache != null) return;

        string cachePath = Path.Combine(GetGlobalModsPath(), "mod-cache.json");
        if (File.Exists(cachePath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(cachePath);
                modCache = JsonSerializer.Deserialize<Dictionary<string, ModCacheEntry>>(json) ?? [];
            }
            catch { modCache = []; }
        }
        else
        {
            modCache = [];
        }
    }

    private async Task SaveCacheAsync()
    {
        if (modCache == null) return;
        string cachePath = Path.Combine(GetGlobalModsPath(), "mod-cache.json");
        try
        {
            JsonSerializerOptions options = new() { WriteIndented = true };
            string json = JsonSerializer.Serialize(modCache, options);
            await File.WriteAllTextAsync(cachePath, json);
        }
        catch { }
    }

    public async Task<ModInfo?> GetCachedModInfoAsync(string modName, bool forceRefresh = false)
    {
        await cacheLock.WaitAsync();
        try
        {
            await LoadCacheAsync();

            if (!forceRefresh && modCache!.TryGetValue(modName, out ModCacheEntry? entry))
            {
                if (DateTime.UtcNow - entry.LastChecked < TimeSpan.FromHours(24))
                {
                    return entry.Info;
                }
            }

            HttpResponseMessage response = await httpClient.GetAsync($"{ModPortalApiBase}/{Uri.EscapeDataString(modName)}");
            ModInfo? info = null;
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };
                info = JsonSerializer.Deserialize<ModInfo>(json, options);
            }

            modCache![modName] = new()
            {
                LastChecked = DateTime.UtcNow,
                Info = info
            };

            await SaveCacheAsync();
            return info;
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private string GetInstanceModsPath(int instanceId)
    {
        return instanceManager.GetModsDirectory(instanceId);
    }

    public string GetGlobalModsPath()
    {
        return instanceManager.GetGlobalModsDirectory();
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
                await using ZipArchive archive = ZipFile.OpenRead(zipPath);
                ZipArchiveEntry? infoEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("info.json", StringComparison.OrdinalIgnoreCase));
                if (infoEntry != null)
                {
                    await using Stream stream = infoEntry.Open();
                    using StreamReader reader = new(stream);
                    string json = reader.ReadToEnd();
                    using JsonDocument doc = JsonDocument.Parse(json);

                    string? name = doc.RootElement.GetProperty("name").GetString();
                    string? version = doc.RootElement.GetProperty("version").GetString();
                    string? title = doc.RootElement.TryGetProperty("title", out JsonElement titleProp) ? titleProp.GetString() : name;

                    if (name != null)
                    {
                        localMods.Add(new()
                        {
                            Name = name,
                            Title = title ?? name,
                            Version = version ?? "0.0.0",
                            FileName = Path.GetFileName(zipPath),
                            IsEnabled = enabledMods.Contains(name),
                            SizeBytes = new FileInfo(zipPath).Length
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
                GlobalSettings settings = settingsService.GetSettings();

                // Fallback: If we don't have the EXACT latest version in the cache, but we DO have an older version
                // of this mod in the cache, and we CANNOT download from the portal because of missing credentials,
                // we should fallback to the best available version in the cache.
                if (string.IsNullOrEmpty(settings.FactorioUsername) || string.IsNullOrEmpty(settings.FactorioToken))
                {
                    string[] cachedMods = Directory.GetFiles(globalModsDir, $"{modName}_*.zip");
                    if (cachedMods.Any())
                    {
                        string fallbackModPath = cachedMods.OrderByDescending(x => x).First();
                        try
                        {
                            targetFilePath = Path.Combine(modsDir, Path.GetFileName(fallbackModPath));
                            File.Copy(fallbackModPath, targetFilePath, true);
                            copiedLocally = true;
                            newFileName = Path.GetFileName(fallbackModPath);
                        }
                        catch { }
                    }
                }

                if (!copiedLocally)
                {
                    if (string.IsNullOrEmpty(settings.FactorioUsername) || string.IsNullOrEmpty(settings.FactorioToken))
                        return (false, "Factorio Mod Portal credentials are not configured in Settings.");

                    string finalUrl = $"https://mods.factorio.com{downloadUrl}?username={Uri.EscapeDataString(settings.FactorioUsername.Trim())}&token={Uri.EscapeDataString(settings.FactorioToken.Trim())}";
                    using HttpResponseMessage response = await httpClient.GetAsync(finalUrl, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode)
                        return (false, $"Mod Portal returned {response.StatusCode} when attempting to download.");

                    await using FileStream fs = new(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await response.Content.CopyToAsync(fs);
                    fs.Close();

                    // Cache in global pool
                    try
                    {
                        File.Copy(targetFilePath, globalModPath, true);
                        cachedGlobalMods = null;
                    }
                    catch { }
                }
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
            cachedGlobalMods = null;
        }
        catch { }
    }

    private List<LocalModInfo>? cachedGlobalMods;
    private DateTime lastGlobalModsDirWriteTime = DateTime.MinValue;
    private readonly SemaphoreSlim globalModsLock = new(1, 1);

    public List<LocalModInfo> GetGlobalMods()
    {
        string modsDir = GetGlobalModsPath();
        if (!Directory.Exists(modsDir)) return [];

        globalModsLock.Wait();
        try
        {
            DateTime currentWriteTime = Directory.GetLastWriteTimeUtc(modsDir);

            // Also check file count just to be slightly more robust, though GetLastWriteTimeUtc usually covers it
            string[] zipFiles = Directory.GetFiles(modsDir, "*.zip");

            if (cachedGlobalMods != null && currentWriteTime == lastGlobalModsDirWriteTime && cachedGlobalMods.Count == zipFiles.Length)
            {
                return cachedGlobalMods.ToList(); // Return a copy
            }

            List<LocalModInfo> globalMods = [];

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
                            globalMods.Add(new()
                            {
                                Name = name,
                                Title = title ?? name,
                                Version = version ?? "0.0.0",
                                FileName = Path.GetFileName(zipPath),
                                IsEnabled = false,
                                SizeBytes = new FileInfo(zipPath).Length
                            });
                        }
                    }
                }
                catch { }
            }
            cachedGlobalMods = globalMods.ToList();
            lastGlobalModsDirWriteTime = currentWriteTime;
            return globalMods;
        }
        finally
        {
            globalModsLock.Release();
        }
    }

    public void DeleteGlobalMod(string fileName)
    {
        try
        {
            string globalPath = Path.Combine(GetGlobalModsPath(), fileName);
            if (File.Exists(globalPath))
            {
                File.Delete(globalPath);
                cachedGlobalMods = null;
            }
        }
        catch { }
    }

    public void ClearUnusedGlobalMods()
    {
        try
        {
            string allInstancesDir = instanceManager.GetAllInstancesDirectory();

            HashSet<string> usedFileNames = [];
            if (Directory.Exists(allInstancesDir))
            {
                IEnumerable<string> otherInstanceModDirs = Directory.GetDirectories(allInstancesDir, "*", SearchOption.TopDirectoryOnly)
                    .Select(d => Path.Combine(d, "mods"))
                    .Where(Directory.Exists);

                foreach (string modDir in otherInstanceModDirs)
                {
                    foreach (string file in Directory.GetFiles(modDir, "*.zip"))
                    {
                        usedFileNames.Add(Path.GetFileName(file));
                    }
                }
            }

            string globalModsDir = GetGlobalModsPath();
            foreach (string file in Directory.GetFiles(globalModsDir, "*.zip"))
            {
                if (!usedFileNames.Contains(Path.GetFileName(file)))
                {
                    File.Delete(file);
                }
            }
            cachedGlobalMods = null;
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
    public long SizeBytes { get; set; }
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
