using System.Net.Http.Json;
using System.Text.Json;
using FactorioLibrary.Internal;
using FactorioLibrary.Models;
using FactorioLibrary.Objects;
using FactorioLibrary.Services;

namespace FactorioLibrary;

public class FactorioWebApi(FactorioCredentials credentials, GlobalSettingsService settingsService)
{
    private const string BaseVersionsUrl = "https://factorio.com/get-available-versions";

    private List<DockerTagInfo>? cachedTags;
    private DateTime lastCacheTime;

    private string VersionsUrl => $"{BaseVersionsUrl}?username={Uri.EscapeDataString(credentials.Username)}&token={Uri.EscapeDataString(credentials.Token)}";

    public async Task<FactorioVersions?> GetVersions()
    {
        string json = await Shared.HttpClient.GetStringAsync(VersionsUrl);
        return JsonSerializer.Deserialize<FactorioVersions>(json);
    }

    public async Task<List<FactorioRelease>> GetReleases()
    {
        FactorioVersions? versions = await GetVersions();
        return versions?.ToReleases() ?? [];
    }

    public async Task<List<DockerTagInfo>> GetDockerTagsAsync()
    {
        try
        {
            GlobalSettings settings = settingsService.GetSettings();

            // Check cache
            if (cachedTags != null && (DateTime.Now - lastCacheTime).TotalHours < 1)
            {
                // We will re-filter the cached tags below based on current settings
            }
            else
            {
                string? url = "https://hub.docker.com/v2/repositories/factoriotools/factorio/tags?page_size=100";
                List<(string Name, string Digest)> rawTags = [];

                string targetArchStr = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "amd64";
                
                int pagesFetched = 0;
                while (!string.IsNullOrEmpty(url) && pagesFetched < 20)
                {
                    JsonElement response = await Shared.HttpClient.GetFromJsonAsync<JsonElement>(url);

                    foreach (JsonElement result in response.GetProperty("results").EnumerateArray())
                    {
                        // Filter out tags that don't support our host architecture
                        bool supportsArch = false;
                        if (result.TryGetProperty("images", out JsonElement imagesProp) && imagesProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement img in imagesProp.EnumerateArray())
                            {
                                if (img.TryGetProperty("architecture", out JsonElement archProp) && archProp.GetString() == targetArchStr)
                                {
                                    supportsArch = true;
                                    break;
                                }
                            }
                        }

                        if (!supportsArch)
                            continue;

                        string name = "";
                        if (result.TryGetProperty("name", out JsonElement nameProp) && nameProp.ValueKind == JsonValueKind.String)
                            name = nameProp.GetString() ?? "";

                        // Factoriotools recently added automated multi-arch manifests, meaning 
                        // they generate an 'arm64' manifest even for Factorio 0.12, but it contains 
                        // an x64 binary that instantly crashes on boot with 'exec format error'.
                        // Factorio officially added arm64 support in late 1.1.x.
                        if (targetArchStr == "arm64" && Version.TryParse(name, out Version? parsedVersion))
                        {
                            if (parsedVersion.Major == 0 || (parsedVersion.Major == 1 && parsedVersion.Minor < 1))
                            {
                                continue; // Skip anything older than 1.1 on ARM
                            }
                        }

                        string digest = "";
                        if (result.TryGetProperty("digest", out JsonElement digestProp) && digestProp.ValueKind == JsonValueKind.String)
                            digest = digestProp.GetString() ?? "";

                        if (!string.IsNullOrEmpty(name))
                        {
                            rawTags.Add((name, digest));
                        }
                    }

                    if (response.TryGetProperty("next", out JsonElement nextProp) && nextProp.ValueKind == JsonValueKind.String)
                        url = nextProp.GetString();
                    else
                        url = null;

                    pagesFetched++;
                }

                // Build a dictionary of digest -> semantic version (so we can see what "latest" points to)
                Dictionary<string, string> digestToVersionMap = [];
                foreach ((string? name, string? digest) in rawTags)
                {
                    if (Version.TryParse(name, out _) && !string.IsNullOrEmpty(digest))
                    {
                        if (!digestToVersionMap.ContainsKey(digest) || string.Compare(name, digestToVersionMap[digest]) > 0)
                            digestToVersionMap[digest] = name;
                    }
                }

                List<DockerTagInfo> tags = [];
                foreach ((string? name, string? digest) in rawTags)
                {
                    if (string.IsNullOrEmpty(name) || name.EndsWith("-rootless")) continue;
                    if (name.StartsWith("stable-")) continue;
                    if (name != "latest" && name != "stable" && name.Count(c => c == '.') < 2) continue;

                    string displayName = name;
                    if ((name == "latest" || name == "stable") && digestToVersionMap.TryGetValue(digest, out string? realVersion))
                        displayName = $"{name} ({realVersion})";

                    tags.Add(new(name, displayName));
                }

                // Sort the base cached tags
                cachedTags = [.. tags.DistinctBy(t => t.Tag).OrderBy(t => t.Tag == "latest" ? 0 : t.Tag == "stable" ? 1 : 2).ThenByDescending(t =>
                    {
                        if (Version.TryParse(t.Tag, out Version? v)) return v;
                        return new(0, 0, 0);
                    })];

                lastCacheTime = DateTime.Now;
            }

            // Now apply filters based on settings
            List<DockerTagInfo> filteredTags = [];
            HashSet<string> seenMinorVersions = [];
            int semanticCount = 0;

            foreach (DockerTagInfo tagInfo in cachedTags)
            {
                string name = tagInfo.Tag;

                if (name == "latest" || name == "stable")
                {
                    filteredTags.Add(tagInfo);
                    continue;
                }

                if (name.StartsWith("0.") && !settings.ShowLegacyVersions)
                    continue; // Skip pre-1.0 if not enabled

                if (settings.ShowAllVersions)
                    filteredTags.Add(tagInfo);
                else
                {
                    // Clean view: Show top 10 semantic releases, then only the newest for each minor line
                    if (semanticCount < 10)
                    {
                        filteredTags.Add(tagInfo);
                        semanticCount++;

                        // Track the minor version line we just added
                        if (Version.TryParse(name, out Version? v))
                            seenMinorVersions.Add($"{v.Major}.{v.Minor}");
                    }
                    else
                    {
                        if (Version.TryParse(name, out Version? v))
                        {
                            string minorLine = $"{v.Major}.{v.Minor}";
                            if (!seenMinorVersions.Contains(minorLine))
                            {
                                filteredTags.Add(tagInfo);
                                seenMinorVersions.Add(minorLine);
                            }
                        }
                    }
                }
            }

            return filteredTags;
        }
        catch
        {
            return [
                new("latest", "Latest"),
                new("stable", "Stable"),
                new("2.1", "2.1"),
                new("2.0", "2.0")
            ];
        }
    }
}
