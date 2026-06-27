using System.Net.Http.Json;
using FactorioLibrary.Internal;
using FactorioLibrary.Objects;
using FactorioLibrary.Services;

namespace FactorioLibrary;

public class FactorioWebApi
{
    private const string BaseVersionsUrl = "https://factorio.com/get-available-versions";

    private readonly FactorioCredentials _credentials;
    private readonly GlobalSettingsService _settingsService;
    private List<DockerTagInfo>? _cachedTags;
    private DateTime _lastCacheTime;

    public FactorioWebApi(FactorioCredentials credentials, GlobalSettingsService settingsService)
    {
        _credentials = credentials;
        _settingsService = settingsService;
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

    public async Task<List<DockerTagInfo>> GetDockerTagsAsync()
    {
        try
        {
            var settings = _settingsService.GetSettings();
            
            // Check cache
            if (_cachedTags != null && (DateTime.Now - _lastCacheTime).TotalHours < 1)
            {
                // We will re-filter the cached tags below based on current settings
            }
            else
            {
                string? url = "https://hub.docker.com/v2/repositories/factoriotools/factorio/tags?page_size=100";
                var rawTags = new List<(string Name, string Digest)>();
                
                int pagesFetched = 0;
                while (!string.IsNullOrEmpty(url) && pagesFetched < 20)
                {
                    var response = await Shared.HttpClient.GetFromJsonAsync<System.Text.Json.JsonElement>(url);
                    
                    bool foundPre10 = false;
                    foreach (var result in response.GetProperty("results").EnumerateArray())
                    {
                        string name = "";
                        if (result.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            name = nameProp.GetString() ?? "";
                        }
                        
                        string digest = "";
                        if (result.TryGetProperty("digest", out var digestProp) && digestProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            digest = digestProp.GetString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(name))
                        {
                            rawTags.Add((name, digest));
                            if (name.StartsWith("0."))
                            {
                                foundPre10 = true;
                            }
                        }
                    }
                    
                    if (response.TryGetProperty("next", out var nextProp) && nextProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        url = nextProp.GetString();
                    }
                    else
                    {
                        url = null;
                    }
                    
                    pagesFetched++;
                    
                    // Stop early if we hit pre-1.0 and don't need legacy versions
                    if (foundPre10 && !settings.ShowLegacyVersions)
                    {
                        break;
                    }
                }
                
                // Build a dictionary of digest -> semantic version (so we can see what "latest" points to)
                var digestToVersionMap = new Dictionary<string, string>();
                foreach (var (name, digest) in rawTags)
                {
                    if (Version.TryParse(name, out _) && !string.IsNullOrEmpty(digest))
                    {
                        if (!digestToVersionMap.ContainsKey(digest) || string.Compare(name, digestToVersionMap[digest]) > 0)
                        {
                            digestToVersionMap[digest] = name;
                        }
                    }
                }

                var tags = new List<DockerTagInfo>();
                foreach (var (name, digest) in rawTags)
                {
                    if (string.IsNullOrEmpty(name) || name.EndsWith("-rootless")) continue;
                    if (name.StartsWith("stable-")) continue;
                    if (name != "latest" && name != "stable" && name.Count(c => c == '.') < 2) continue;

                    string displayName = name;
                    if ((name == "latest" || name == "stable") && digestToVersionMap.TryGetValue(digest, out string? realVersion))
                    {
                        displayName = $"{name} ({realVersion})";
                    }

                    tags.Add(new DockerTagInfo(name, displayName));
                }
                
                // Sort the base cached tags
                _cachedTags = tags
                    .DistinctBy(t => t.Tag)
                    .OrderBy(t => t.Tag == "latest" ? 0 : t.Tag == "stable" ? 1 : 2)
                    .ThenByDescending(t => {
                        if (Version.TryParse(t.Tag, out var v)) return v;
                        return new Version(0, 0, 0);
                    })
                    .ToList();
                    
                _lastCacheTime = DateTime.Now;
            }
            
            // Now apply filters based on settings
            var filteredTags = new List<DockerTagInfo>();
            var seenMinorVersions = new HashSet<string>();
            int semanticCount = 0;

            foreach (var tagInfo in _cachedTags)
            {
                string name = tagInfo.Tag;
                
                if (name == "latest" || name == "stable")
                {
                    filteredTags.Add(tagInfo);
                    continue;
                }

                if (name.StartsWith("0.") && !settings.ShowLegacyVersions)
                {
                    continue; // Skip pre-1.0 if not enabled
                }

                if (settings.ShowAllVersions)
                {
                    filteredTags.Add(tagInfo);
                }
                else
                {
                    // Clean view: Show top 10 semantic releases, then only the newest for each minor line
                    if (semanticCount < 10)
                    {
                        filteredTags.Add(tagInfo);
                        semanticCount++;
                        
                        // Track the minor version line we just added
                        if (Version.TryParse(name, out var v))
                        {
                            seenMinorVersions.Add($"{v.Major}.{v.Minor}");
                        }
                    }
                    else
                    {
                        if (Version.TryParse(name, out var v))
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
            return new List<DockerTagInfo> { 
                new("latest", "latest"), 
                new("stable", "stable"), 
                new("2.1", "2.1"), 
                new("2.0", "2.0") 
            };
        }
    }
}
