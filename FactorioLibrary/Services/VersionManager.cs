using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using FactorioLibrary.Objects;
using FactorioLibrary.Internal;
using System.Collections.Generic;
using System.Linq;

namespace FactorioLibrary.Services;

public class VersionManager
{
    private readonly FactorioWebApi _webApi;
    private readonly string _versionsDirectory;
    private readonly HttpClient _httpClient;

    public VersionManager(FactorioWebApi webApi, string versionsDirectory = "factorio_versions", HttpClient? httpClient = null)
    {
        _webApi = webApi;
        _versionsDirectory = versionsDirectory;
        _httpClient = httpClient ?? Shared.HttpClient;
        if (!Directory.Exists(_versionsDirectory))
        {
            Directory.CreateDirectory(_versionsDirectory);
        }
    }

    public async Task<string> DownloadVersionAsync(FactorioRelease release)
    {
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
        string filePath = Path.Combine(_versionsDirectory, $"factorio_{versionStr}_{release.Platform}.{extension}");

        if (File.Exists(filePath))
        {
            return filePath; // Already downloaded
        }

        // This is a placeholder for the actual download implementation which would write the stream to a file.
        // We will mock this in tests.
        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fs);

        return filePath;
    }
}
