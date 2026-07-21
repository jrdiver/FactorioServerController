using System.ComponentModel.DataAnnotations;

namespace FactorioLibrary.Models;

public enum ServerAccessLevel
{
    Viewer = 0,
    Admin = 1
}

public class UserServerAccess
{
    [Key]
    public int Id { get; set; }
    
    [Required]
    public string UserId { get; set; } = string.Empty;
    
    public int ServerInstanceId { get; set; }

    public ServerAccessLevel AccessLevel { get; set; } = ServerAccessLevel.Viewer;
}
