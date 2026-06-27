using Microsoft.VisualStudio.TestTools.UnitTesting;
using FactorioLibrary.Services;
using FactorioLibrary.Objects;
using FactorioLibrary;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.IO;
using System;

namespace FactorioServerTest;

public class MockHttpMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public HttpResponseMessage ResponseToReturn { get; set; } = new HttpResponseMessage(HttpStatusCode.OK);
    
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(ResponseToReturn);
    }
}

[TestClass]
public class VersionManagerTests
{
    private string _tempDir = "";

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [TestMethod]
    public async Task DownloadVersionAsync_UsesCorrectUrl_AndSavesFile()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.ResponseToReturn.Content = new StringContent("fake binary data");
        var httpClient = new HttpClient(mockHandler);
        
        var settingsService = new GlobalSettingsService("test_settings.json");
        var credentials = new FactorioCredentials { Username = "user", Token = "token" };
        var webApi = new FactorioWebApi(credentials, settingsService);
        var manager = new VersionManager(webApi, _tempDir, httpClient);

        var release = new FactorioRelease 
        { 
            Version = new Version(1, 1, 107), 
            Platform = "core-linux_headless64",
            Os = PlatformOs.Linux
        };

        // Act
        string filePath = await manager.DownloadVersionAsync(release);

        // Assert
        Assert.IsNotNull(mockHandler.LastRequest);
        Assert.AreEqual("https://factorio.com/get-download/1.1.107/headless/linux64", mockHandler.LastRequest.RequestUri?.ToString());
        Assert.IsTrue(File.Exists(filePath));
        
        string savedContent = await File.ReadAllTextAsync(filePath);
        Assert.AreEqual("fake binary data", savedContent);
    }
}
