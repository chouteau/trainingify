using System.ComponentModel.DataAnnotations;

namespace Velodromify.Data;

public class RideSession
{
    [Key]
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public DateTime StartTime { get; set; } = DateTime.Now;
    public DateTime? EndTime { get; set; }

    public double TotalDistanceMeters { get; set; }
    public int Laps { get; set; }
    
    // Average stats
    public double AverageSpeedKmh { get; set; }
    public double AveragePowerWatts { get; set; }
    public double AverageHeartRateBpm { get; set; }
}
