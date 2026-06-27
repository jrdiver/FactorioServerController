using System.ComponentModel.DataAnnotations;

namespace FactorioLibrary.Models;

public class ServerInstance
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public int Port { get; set; } = 34197;

    public int RconPort { get; set; } = 27015;

    [MaxLength(100)]
    public string RconPassword { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? AssignedVersion { get; set; }

    public int MaxPlayers { get; set; } = 0; // 0 means unlimited

    public string? Description { get; set; }
}
