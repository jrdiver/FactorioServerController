using System.Text.Json;
using FactorioLibrary.Models;

namespace FactorioLibrary.Services;

public class GlobalSettingsService(string settingsFilePath = "settings.json")
{
    private GlobalSettings cachedSettings = LoadSettingsSync(settingsFilePath);

    public GlobalSettings GetSettings() => cachedSettings;

    public async Task SaveSettingsAsync(GlobalSettings settings)
    {
        cachedSettings = settings;
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
