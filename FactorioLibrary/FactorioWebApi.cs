using FactorioLibrary.Internal;
using FactorioLibrary.Objects;

namespace FactorioLibrary;

public class FactorioWebApi
{
    private const string BaseVersionsUrl = "https://factorio.com/get-available-versions";

    private readonly FactorioCredentials _credentials;

    public FactorioWebApi(FactorioCredentials credentials)
    {
        _credentials = credentials;
    }

    private string VersionsUrl =>
        $"{BaseVersionsUrl}?username={Uri.EscapeDataString(_credentials.Username)}&token={Uri.EscapeDataString(_credentials.Token)}";

    public async Task<FactorioVersions?> GetVersions()
    {
        string json = await Shared.HttpClient.GetStringAsync(VersionsUrl);
        return System.Text.Json.JsonSerializer.Deserialize<FactorioVersions>(json);
    }

    public async Task<List<FactorioRelease>> GetReleases()
    {
        FactorioVersions? versions = await GetVersions();
        return versions?.ToReleases() ?? [];
    }
}
