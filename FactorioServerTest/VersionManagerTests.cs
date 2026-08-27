using System.Net;
using FactorioLibrary;
using FactorioLibrary.Objects;
using FactorioLibrary.Services;

namespace FactorioServerTest;

public class MockHttpMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public HttpResponseMessage ResponseToReturn { get; set; } = new(HttpStatusCode.OK);
    
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(ResponseToReturn);
    }
}

[TestClass]
public class VersionManagerTests
{
    private string tempDir = "";

    [TestInitialize]
    public void Setup()
    {
        tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, true);
        }
    }

    [TestMethod]
    public async Task DownloadVersionAsync_UsesCorrectUrl_AndSavesFile()
    {
        // Arrange
        MockHttpMessageHandler mockHandler = new();
        mockHandler.ResponseToReturn.Content = new StringContent("fake binary data");
        HttpClient httpClient = new(mockHandler);

        GlobalSettingsService settingsService = new("test_settings.json");
        FactorioCredentials credentials = new() { Username = "user", Token = "token" };
        FactorioWebApi webApi = new(credentials, settingsService);
        VersionManager manager = new(webApi, tempDir, httpClient);

        FactorioRelease release = new()
        { 
            Version = new(1, 1, 107), 
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
