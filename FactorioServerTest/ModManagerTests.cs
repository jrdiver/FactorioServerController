using Microsoft.VisualStudio.TestTools.UnitTesting;
using FactorioLibrary.Services;
using System.Net.Http;
using System.Threading.Tasks;
using System.Net;

namespace FactorioServerTest;

[TestClass]
public class ModManagerTests
{
    [TestMethod]
    public async Task GetModInfoAsync_ReturnsParsedModInfo()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
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
        var httpClient = new HttpClient(mockHandler);
        var manager = new ModManager(httpClient);

        // Act
        var result = await manager.GetModInfoAsync("bobinserters");

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
    public async Task GetModInfoAsync_ReturnsNullOnNotFound()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.ResponseToReturn.StatusCode = HttpStatusCode.NotFound;
        var httpClient = new HttpClient(mockHandler);
        var manager = new ModManager(httpClient);

        // Act
        var result = await manager.GetModInfoAsync("nonexistent-mod");

        // Assert
        Assert.IsNull(result);
    }
}
