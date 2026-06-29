using FactorioLibrary.Models;
using FactorioLibrary.Services;

namespace FactorioServerTest;

[TestClass]
public class GlobalSettingsServiceTests
{
    private const string TestSettingsPath = "temp_settings.json";

    [TestCleanup]
    public void Cleanup()
    {
        if (File.Exists(TestSettingsPath))
        {
            File.Delete(TestSettingsPath);
        }
    }

    [TestMethod]
    public void Constructor_ShouldLoadDefaultsIfNoFileExists()
    {
        // Act
        GlobalSettingsService service = new(TestSettingsPath);
        GlobalSettings settings = service.GetSettings();

        // Assert
        Assert.IsNotNull(settings);
        Assert.IsFalse(settings.ShowAllVersions); // Defaults
        Assert.IsFalse(settings.ShowLegacyVersions);
    }

    [TestMethod]
    public async Task SaveSettingsAsync_ShouldWriteToFile()
    {
        // Arrange
        GlobalSettingsService service = new(TestSettingsPath);
        GlobalSettings settings = new() { ShowAllVersions = true, ShowLegacyVersions = true };

        // Act
        await service.SaveSettingsAsync(settings);

        // Assert
        Assert.IsTrue(File.Exists(TestSettingsPath));
        GlobalSettingsService loadedService = new(TestSettingsPath);
        GlobalSettings loadedSettings = loadedService.GetSettings();

        Assert.IsTrue(loadedSettings.ShowAllVersions);
        Assert.IsTrue(loadedSettings.ShowLegacyVersions);
    }
}
