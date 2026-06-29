using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using FactorioLibrary.Internal;
using FactorioLibrary.Models;
using Microsoft.Extensions.Configuration;

namespace FactorioLibrary.Services;

public class ModManager
{
    private const string ModPortalApiBase = "https://mods.factorio.com/api/mods";
    private readonly HttpClient _httpClient;
    private readonly GlobalSettingsService _settingsService;
    private readonly string _hostBaseMountPath;

    public ModManager(IConfiguration config, GlobalSettingsService settingsService, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? Shared.HttpClient;
        _settingsService = settingsService;
        _hostBaseMountPath = config.GetValue<string>("HOST_BASE_MOUNT_PATH") 
            ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "C:\\FactorioServers" : "/mnt/user/appdata/factorio_manager/servers");
    }

    public async Task<ModInfo?> GetModInfoAsync(string modName)
    {
        var response = await _httpClient.GetAsync($"{ModPortalApiBase}/{Uri.EscapeDataString(modName)}");
        if (!response.IsSuccessStatusCode)
            return null;

        string json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ModInfo>(json);
    }

    private string GetInstanceModsPath(int instanceId)
    {
        string localDataPath = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" 
            ? $"/factorio/{instanceId}" 
            : $"{_hostBaseMountPath.TrimEnd('/', '\\')}/{instanceId}";
        return Path.Combine(localDataPath, "mods");
    }

    public async Task<List<LocalModInfo>> GetLocalModsAsync(int instanceId)
    {
        var modsDir = GetInstanceModsPath(instanceId);
        if (!Directory.Exists(modsDir)) return new List<LocalModInfo>();

        var localMods = new List<LocalModInfo>();
        var zipFiles = Directory.GetFiles(modsDir, "*.zip");

        // Parse mod-list.json to see what is enabled
        var enabledMods = new HashSet<string>();
        var modListPath = Path.Combine(modsDir, "mod-list.json");
        if (File.Exists(modListPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(modListPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("mods", out var modsArray))
                {
                    foreach (var mod in modsArray.EnumerateArray())
                    {
                        var name = mod.GetProperty("name").GetString();
                        var isEnabled = mod.GetProperty("enabled").GetBoolean();
                        if (isEnabled && name != null) enabledMods.Add(name);
                    }
                }
            }
            catch { }
        }

        foreach (var zipPath in zipFiles)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                var infoEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("info.json", StringComparison.OrdinalIgnoreCase));
                if (infoEntry != null)
                {
                    using var stream = infoEntry.Open();
                    using var reader = new StreamReader(stream);
                    var json = reader.ReadToEnd();
                    using var doc = JsonDocument.Parse(json);
                    
                    var name = doc.RootElement.GetProperty("name").GetString();
                    var version = doc.RootElement.GetProperty("version").GetString();
                    var title = doc.RootElement.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : name;

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
        var settings = _settingsService.GetSettings();
        if (string.IsNullOrEmpty(settings.FactorioUsername) || string.IsNullOrEmpty(settings.FactorioToken))
        {
            return (false, "Factorio Mod Portal credentials are not configured in Settings.");
        }

        var modsDir = GetInstanceModsPath(instanceId);
        if (!Directory.Exists(modsDir)) Directory.CreateDirectory(modsDir);

        string finalUrl = $"https://mods.factorio.com{downloadUrl}?username={Uri.EscapeDataString(settings.FactorioUsername.Trim())}&token={Uri.EscapeDataString(settings.FactorioToken.Trim())}";
        string targetFilePath = Path.Combine(modsDir, newFileName);

        try
        {
            using var response = await _httpClient.GetAsync(finalUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"Mod Portal returned {response.StatusCode} when attempting to download.");
            }

            using var fs = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fs);
            
            // Delete old file if successful and it's a different file
            if (!string.IsNullOrEmpty(oldFileName) && oldFileName != newFileName)
            {
                var oldFilePath = Path.Combine(modsDir, oldFileName);
                if (File.Exists(oldFilePath))
                {
                    File.Delete(oldFilePath);
                }
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"Download failed: {ex.Message}");
        }
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

    [JsonPropertyName("releases")]
    public List<ModRelease> Releases { get; set; } = new();
}

public class ModRelease
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;
}
