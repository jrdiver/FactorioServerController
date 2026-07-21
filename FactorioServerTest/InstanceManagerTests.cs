using FactorioLibrary.Services;
using Microsoft.Extensions.Configuration;

namespace FactorioServerTest;

[TestClass]
public class InstanceManagerTests
{
    private IConfiguration CreateConfig(string hostBasePath)
    {
        Dictionary<string, string?> inMemorySettings = new()
        {
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
        IConfiguration config = CreateConfig("/custom/host/path");
        InstanceManager manager = new(config, new RconService(), null!);

        // Act
        string result = manager.GetSavesDirectory(42);

        // Assert
        StringAssert.Contains(result, "42");
        Assert.IsTrue(result.EndsWith("saves") || result.EndsWith("saves\\"));
    }

    [TestMethod]
    public void GetModsDirectory_ShouldReturnCorrectPath()
    {
        // Arrange
        IConfiguration config = CreateConfig("/custom/host/path");
        InstanceManager manager = new(config, new RconService(), null!);

        // Act
        string result = manager.GetModsDirectory(99);

        // Assert
        StringAssert.Contains(result, "99");
        Assert.IsTrue(result.EndsWith("mods") || result.EndsWith("mods\\"));
    }

    [TestMethod]
    public void IsRunning_ShouldReturnFalseForUnknownId()
    {
        // Arrange
        IConfiguration config = CreateConfig("/custom/host/path");
        InstanceManager manager = new(config, new RconService(), null!);

        // Act
        bool isRunning = manager.IsRunning(9999);

        // Assert
        Assert.IsFalse(isRunning);
    }
}
