using System.ComponentModel.DataAnnotations;

namespace Velodromify.Data;

public class User
{
    [Key]
    public int Id { get; set; }

    [Required]
    [StringLength(50, MinimumLength = 3)]
    public string Pseudo { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<RideSession> RideSessions { get; set; } = new();
}
