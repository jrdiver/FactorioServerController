using FactorioLibrary.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;

namespace FactorioServerTest;

[TestClass]
public class InstanceManagerTests
{
    private IConfiguration CreateConfig(string hostBasePath)
    {
        var inMemorySettings = new Dictionary<string, string?> {
            {"HOST_BASE_MOUNT_PATH", hostBasePath}
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();
    }

    [TestMethod]
    public void GetSavesDirectory_ShouldReturnCorrectPath()
    {
        // Arrange
        var config = CreateConfig("/custom/host/path");
        var manager = new InstanceManager(config);

        // Act
        var result = manager.GetSavesDirectory(42);

        // Assert
        StringAssert.Contains(result, "42");
        Assert.IsTrue(result.EndsWith("saves") || result.EndsWith("saves\\"));
    }

    [TestMethod]
    public void GetModsDirectory_ShouldReturnCorrectPath()
    {
        // Arrange
        var config = CreateConfig("/custom/host/path");
        var manager = new InstanceManager(config);

        // Act
        var result = manager.GetModsDirectory(99);

        // Assert
        StringAssert.Contains(result, "99");
        Assert.IsTrue(result.EndsWith("mods") || result.EndsWith("mods\\"));
    }

    [TestMethod]
    public void IsRunning_ShouldReturnFalseForUnknownId()
    {
        // Arrange
        var config = CreateConfig("/custom/host/path");
        var manager = new InstanceManager(config);

        // Act
        var isRunning = manager.IsRunning(9999);

        // Assert
        Assert.IsFalse(isRunning);
    }
}
