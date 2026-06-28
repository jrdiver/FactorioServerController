using FactorioLibrary.Models;
using FactorioLibrary.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Threading.Tasks;

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
        var service = new GlobalSettingsService(TestSettingsPath);
        var settings = service.GetSettings();

        // Assert
        Assert.IsNotNull(settings);
        Assert.IsFalse(settings.ShowAllVersions); // Defaults
        Assert.IsFalse(settings.ShowLegacyVersions);
    }

    [TestMethod]
    public async Task SaveSettingsAsync_ShouldWriteToFile()
    {
        // Arrange
        var service = new GlobalSettingsService(TestSettingsPath);
        var settings = new GlobalSettings { ShowAllVersions = true, ShowLegacyVersions = true };

        // Act
        await service.SaveSettingsAsync(settings);

        // Assert
        Assert.IsTrue(File.Exists(TestSettingsPath));
        var loadedService = new GlobalSettingsService(TestSettingsPath);
        var loadedSettings = loadedService.GetSettings();

        Assert.IsTrue(loadedSettings.ShowAllVersions);
        Assert.IsTrue(loadedSettings.ShowLegacyVersions);
    }
}
