using System.Text.Json.Serialization;
using FactorioLibrary.Internal;

namespace FactorioLibrary.Objects;

public enum PlatformOs { Linux, Windows, Mac, Unknown }
public enum PlatformArchitecture { X86, X64, Arm64, Unknown }

// Maps raw API platform keys to human-readable display names.
// Any key not listed here is auto-formatted as a fallback.
[JsonConverter(typeof(FactorioVersionsConverter))]
public class FactorioVersions
{
    private static readonly Dictionary<string, string> KnownPlatformNames = new(StringComparer.OrdinalIgnoreCase)
    {
        { "core-linux32",             "Linux 32-bit" },
        { "core-linux64",             "Linux 64-bit" },
        { "core-linux_headless64",    "Linux Headless 64-bit" },
        { "core-mac",                 "Mac" },
        { "core-mac-arm64",           "Mac ARM64" },
        { "core-mac-x64",             "Mac x64" },
        { "core-win32",               "Windows 32-bit" },
        { "core-win64",               "Windows 64-bit" },
        { "core_expansion-linux64",   "Expansion Linux 64-bit" },
        { "core_expansion-mac",       "Expansion Mac" },
        { "core_expansion-win64",     "Expansion Windows 64-bit" },
    };

    public Dictionary<string, List<FactorioVersion>> Platforms { get; init; } = [];

    public static string ResolvePlatformName(string key) => KnownPlatformNames.TryGetValue(key, out string? name) ? name : System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(key.Replace('-', ' ').Replace('_', ' '));

    public static PlatformOs ResolveOs(string key)
    {
        if (key.Contains("linux", StringComparison.OrdinalIgnoreCase)) return PlatformOs.Linux;
        if (key.Contains("win", StringComparison.OrdinalIgnoreCase)) return PlatformOs.Windows;
        if (key.Contains("mac", StringComparison.OrdinalIgnoreCase)) return PlatformOs.Mac;
        return PlatformOs.Unknown;
    }

    public static PlatformArchitecture ResolveArchitecture(string key)
    {
        if (key.Contains("arm64", StringComparison.OrdinalIgnoreCase)) return PlatformArchitecture.Arm64;
        if (key.Contains("64", StringComparison.OrdinalIgnoreCase)) return PlatformArchitecture.X64;
        if (key.Contains("32", StringComparison.OrdinalIgnoreCase) || key.Contains("x86", StringComparison.OrdinalIgnoreCase)) return PlatformArchitecture.X86;
        return PlatformArchitecture.Unknown;
    }

    public static bool ResolveHeadless(string key) => key.Contains("headless", StringComparison.OrdinalIgnoreCase);

    public List<FactorioRelease> ToReleases()
    {
        List<FactorioRelease> releases = [];
        foreach ((string key, List<FactorioVersion> versions) in Platforms)
        {
            if (versions is not { Count: > 0 }) continue;

            HashSet<string> stableVersions = versions.Where(v => !string.IsNullOrEmpty(v.Stable)).Select(v => v.Stable!).ToHashSet(StringComparer.Ordinal);

            // Union "from" and "to" so the oldest version (only ever a "from") is not lost.
            HashSet<string> allVersionStrings = versions.SelectMany(v => new[] { v.From, v.To }).Where(v => !string.IsNullOrEmpty(v)).ToHashSet(StringComparer.Ordinal);

            IEnumerable<FactorioRelease> platformReleases = allVersionStrings
                .Select(v => new FactorioRelease
                {
                    Version = Version.Parse(v),
                    Platform = key,
                    Os = ResolveOs(key),
                    Architecture = ResolveArchitecture(key),
                    IsHeadless = ResolveHeadless(key),
                    IsStable = stableVersions.Contains(v),
                });

            releases.AddRange(platformReleases);
        }
        return releases;
    }
}

public class FactorioVersion
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("stable")]
    public string? Stable { get; set; }
}

public class FactorioRelease
{
    public Version Version { get; set; } = new();
    /// <summary>Raw API platform key (e.g. "core-linux_headless64").</summary>
    public string Platform { get; set; } = string.Empty;
    /// <summary>Human-readable display name derived from the platform key.</summary>
    public string DisplayName => FactorioVersions.ResolvePlatformName(Platform);
    public PlatformOs Os { get; set; }
    public PlatformArchitecture Architecture { get; set; }
    public bool IsHeadless { get; set; }
    public bool IsStable { get; set; }
}

