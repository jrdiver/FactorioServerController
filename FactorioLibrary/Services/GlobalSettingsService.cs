using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FactorioLibrary.Models;

namespace FactorioLibrary.Services;

public class GlobalSettingsService
{
    private readonly string _settingsFilePath;
    private GlobalSettings _cachedSettings;

    public GlobalSettingsService(string settingsFilePath = "settings.json")
    {
        _settingsFilePath = settingsFilePath;
        _cachedSettings = LoadSettingsSync();
    }

    public GlobalSettings GetSettings()
    {
        return _cachedSettings;
    }

    public async Task SaveSettingsAsync(GlobalSettings settings)
    {
        _cachedSettings = settings;
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_settingsFilePath, json);
    }

    private GlobalSettings LoadSettingsSync()
    {
        if (File.Exists(_settingsFilePath))
        {
            try
            {
                string json = File.ReadAllText(_settingsFilePath);
                return JsonSerializer.Deserialize<GlobalSettings>(json) ?? new GlobalSettings();
            }
            catch
            {
                return new GlobalSettings();
            }
        }
        return new GlobalSettings();
    }
}
