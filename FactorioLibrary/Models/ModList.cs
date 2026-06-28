using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FactorioLibrary.Models;

public class ModList
{
    [JsonPropertyName("mods")]
    public List<ModEntry> Mods { get; set; } = new();
}

public class ModEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}
