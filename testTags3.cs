using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

class Program
{
    static async System.Threading.Tasks.Task Main()
    {
        var httpClient = new HttpClient();
        string url = "https://hub.docker.com/v2/repositories/factoriotools/factorio/tags?page_size=100";
        var rawTags = new List<string>();
        string targetArchStr = "amd64";
        int pagesFetched = 0;
        
        while (!string.IsNullOrEmpty(url) && pagesFetched < 20)
        {
            var response = await httpClient.GetFromJsonAsync<JsonElement>(url);
            bool foundPre10 = false;
            foreach (var result in response.GetProperty("results").EnumerateArray())
            {
                bool supportsArch = false;
                if (result.TryGetProperty("images", out JsonElement imagesProp) && imagesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var img in imagesProp.EnumerateArray())
                    {
                        if (img.TryGetProperty("architecture", out var archProp) && archProp.GetString() == targetArchStr)
                        {
                            supportsArch = true;
                            break;
                        }
                    }
                }
                if (!supportsArch) continue;
                
                string name = result.GetProperty("name").GetString();
                rawTags.Add(name);
                if (name.StartsWith("0.")) foundPre10 = true;
            }
            if (response.TryGetProperty("next", out var nextProp) && nextProp.ValueKind == JsonValueKind.String)
                url = nextProp.GetString();
            else
                url = null;
                
            pagesFetched++;
        }
        
        var tags = rawTags.Where(n => !n.EndsWith("-rootless") && !n.StartsWith("stable-") && (n == "latest" || n == "stable" || n.Count(c => c == '.') >= 2)).ToList();
        
        var sorted = tags.Distinct().OrderBy(t => t == "latest" ? 0 : t == "stable" ? 1 : 2).ThenByDescending(t => 
        {
            if (Version.TryParse(t, out Version v)) return v;
            return new Version(0,0,0);
        }).ToList();
        
        foreach (var t in sorted) {
            if (t.StartsWith("1.1.1") || t == "1.1.75")
                Console.WriteLine(t);
        }
    }
}
