using FactorioLibrary.Services;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace FactorioServerTest;

[TestClass]
public class ModManagerTests
{
    [TestMethod]
    public async Task GetCachedModInfoAsync_ReturnsParsedModInfo()
    {
        // Arrange
        MockHttpMessageHandler mockHandler = new();
        mockHandler.ResponseToReturn.Content = new StringContent(@"
        {
            ""name"": ""bobinserters"",
            ""title"": ""Bob's Inserters"",
            ""summary"": ""Adds more inserters."",
            ""downloads_count"": 123456,
            ""releases"": [
                {
                    ""version"": ""1.0.0"",
                    ""download_url"": ""/download/url"",
                    ""file_name"": ""bobinserters_1.0.0.zip""
                }
            ]
        }");
        HttpClient httpClient = new(mockHandler);
        
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        ModManager manager = new(config, null!, httpClient);

        // Act
        ModInfo? result = await manager.GetCachedModInfoAsync("bobinserters", forceRefresh: true);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual("bobinserters", result.Name);
        Assert.AreEqual("Bob's Inserters", result.Title);
        Assert.AreEqual(123456, result.DownloadsCount);
        Assert.HasCount(1, result.Releases);
        Assert.AreEqual("1.0.0", result.Releases[0].Version);
        
        Assert.IsNotNull(mockHandler.LastRequest);
        Assert.AreEqual("https://mods.factorio.com/api/mods/bobinserters", mockHandler.LastRequest.RequestUri?.ToString());
    }
    
    [TestMethod]
    public async Task GetCachedModInfoAsync_ReturnsNullOnNotFound()
    {
        // Arrange
        MockHttpMessageHandler mockHandler = new();
        mockHandler.ResponseToReturn.StatusCode = HttpStatusCode.NotFound;
        HttpClient httpClient = new(mockHandler);
        
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        ModManager manager = new(config, null!, httpClient);

        // Act
        ModInfo? result = await manager.GetCachedModInfoAsync("nonexistent-mod", forceRefresh: true);

        // Assert
        Assert.IsNull(result);
    }
}
