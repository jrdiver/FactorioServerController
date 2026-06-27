using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FactorioLibrary.Internal;

namespace FactorioLibrary.Services;

public class ModManager
{
    private const string ModPortalApiBase = "https://mods.factorio.com/api/mods";
    private readonly HttpClient _httpClient;

    public ModManager(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? Shared.HttpClient;
    }

    public async Task<ModInfo?> GetModInfoAsync(string modName)
    {
        var response = await _httpClient.GetAsync($"{ModPortalApiBase}/{Uri.EscapeDataString(modName)}");
        if (!response.IsSuccessStatusCode)
            return null;

        string json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ModInfo>(json);
    }
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
