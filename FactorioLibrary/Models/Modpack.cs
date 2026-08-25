using System.ComponentModel.DataAnnotations;

namespace FactorioLibrary.Models;

public class Modpack
{
    public int Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public string TargetFactorioVersion { get; set; } = "latest";

    // Store raw mod-list.json contents
    public string ModListJson { get; set; } = string.Empty;

    // Store raw mod-settings.dat binary contents
    public byte[]? ModSettingsDat { get; set; }
}
