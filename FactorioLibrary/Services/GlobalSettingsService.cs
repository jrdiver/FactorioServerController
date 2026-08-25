using FactorioLibrary.Models;
using System.Text.Json;

namespace FactorioLibrary.Services;

public class GlobalSettingsService(string settingsFilePath = "settings.json")
{
    private GlobalSettings _cachedSettings = LoadSettingsSync(settingsFilePath);

    public GlobalSettings GetSettings() => _cachedSettings;

    public async Task SaveSettingsAsync(GlobalSettings settings)
    {
        _cachedSettings = settings;
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(settingsFilePath, json);
    }

    private static GlobalSettings LoadSettingsSync(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                return JsonSerializer.Deserialize<GlobalSettings>(File.ReadAllText(path)) ?? new();
            }
            catch
            {
                return new();
            }
        }
        return new();
    }
}
