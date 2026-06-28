using System.ComponentModel.DataAnnotations;

namespace FactorioLibrary.Models;

public class UserServerAccess
{
    [Key]
    public int Id { get; set; }
    
    [Required]
    public string UserId { get; set; } = string.Empty;
    
    public int ServerInstanceId { get; set; }
}
