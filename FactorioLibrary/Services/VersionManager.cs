using FactorioLibrary.Objects;
using FactorioLibrary.Internal;

namespace FactorioLibrary.Services;

public class VersionManager(FactorioWebApi webApi, string versionsDirectory = "factorio_versions", HttpClient? httpClient = null)
{
    private readonly FactorioWebApi _webApi = webApi;
    private readonly HttpClient _httpClient = httpClient ?? Shared.HttpClient;

    public async Task<string> DownloadVersionAsync(FactorioRelease release)
    {
        if (!Directory.Exists(versionsDirectory))
            Directory.CreateDirectory(versionsDirectory);

        string buildString = release.Platform switch
        {
            "core-linux_headless64" => "headless/linux64",
            "core-win64" => "alpha/win64-manual",
            "core-mac-x64" => "alpha/mac-x64",
            "core-mac-arm64" => "alpha/mac-arm64",
            _ => release.Platform.Replace("core-", "").Replace("core_expansion-", "")
        };

        // URL format: https://factorio.com/get-download/{version}/{build}?username={user}&token={token}
        // FactorioWebApi would ideally handle the URL generation, but we can do it here for now or update FactorioWebApi.
        
        string versionStr = release.Version.ToString(3); // typically x.y.z
        string downloadUrl = $"https://factorio.com/get-download/{versionStr}/{buildString}";
        
        // Let's assume we want to download to a temp file and extract
        string extension = release.Os == PlatformOs.Windows ? "zip" : "tar.xz";
        string filePath = Path.Combine(versionsDirectory, $"factorio_{versionStr}_{release.Platform}.{extension}");

        if (File.Exists(filePath))
            return filePath; // Already downloaded

        // This is a placeholder for the actual download implementation which would write the stream to a file.
        // We will mock this in tests.
        using HttpResponseMessage response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using FileStream fs = new(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fs);

        return filePath;
    }
}
