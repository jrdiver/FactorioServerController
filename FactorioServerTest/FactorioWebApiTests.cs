using FactorioLibrary;
using FactorioLibrary.Objects;

namespace FactorioServerTest;

[TestClass]
public sealed class FactorioWebApiTests
{
    private static readonly FactorioWebApi WebApi = new(new("jrdiver", "f357fe961a77af8545be75be26e17b"), new("test_settings.json"));

    [TestMethod]
    public async Task GetVersions_ShouldReturnValidVersionList()
    {
        FactorioVersions? results = await WebApi.GetVersions();
        Assert.IsNotNull(results);
        Assert.IsNotNull(results.Platforms);
        Assert.IsNotEmpty(results.Platforms);
    }

    [TestMethod]
    public async Task GetReleases_ShouldReturnReleases()
    {
        List<FactorioRelease> results = await WebApi.GetReleases();
        Assert.IsNotNull(results);
        Assert.IsNotEmpty(results);
    }
}