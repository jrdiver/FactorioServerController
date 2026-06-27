using FactorioLibrary;
using FactorioLibrary.Objects;

namespace FactorioServerTest;

[TestClass]
public sealed class Test1
{
    private static readonly FactorioWebApi WebApi = new(new("jrdiver", "f357fe961a77af8545be75be26e17b"), new FactorioLibrary.Services.GlobalSettingsService("test_settings.json"));

    [TestMethod]
    public void RconConnection()
    {
        FactorioConnector connector = new();
        Task info = connector.GetServerInfo();
        info.GetAwaiter().GetResult();
    }

    [TestMethod]
    public void GetVersions()
    {
        Task<FactorioVersions?> versions = WebApi.GetVersions();
        FactorioVersions? results = versions.GetAwaiter().GetResult();
        Assert.IsNotNull(results);
    }

    [TestMethod]
    public void GetReleases()
    {
        Task<List<FactorioRelease>> versions = WebApi.GetReleases();
        List<FactorioRelease> results = versions.GetAwaiter().GetResult();
        Assert.IsNotNull(results);
    }
}