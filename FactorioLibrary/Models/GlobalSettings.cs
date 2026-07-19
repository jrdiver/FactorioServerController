namespace FactorioLibrary.Models;

public class GlobalSettings
{
    public bool ShowAllVersions { get; set; } = false;
    public bool ShowLegacyVersions { get; set; } = false;
    public string FactorioUsername { get; set; } = string.Empty;
    public string FactorioToken { get; set; } = string.Empty;
    public int ShutdownTimeoutSeconds { get; set; } = 60;
}
