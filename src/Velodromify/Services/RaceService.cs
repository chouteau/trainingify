using System.Collections.Concurrent;
using Velodromify.Data;

namespace Velodromify.Services;

public class RaceService
{
    // Active riders: UserId -> RiderState
    private readonly ConcurrentDictionary<int, RiderState> _riders = new();
    
    public event Action? OnStateChanged;

    public void RegisterRider(User user)
    {
        if (!_riders.ContainsKey(user.Id))
        {
            _riders.TryAdd(user.Id, new RiderState { User = user });
            NotifyStateChanged();
        }
    }

    public void UpdateRiderProgress(int userId, double speedKmh, double powerWatts, double heartRate, double deltaSeconds)
    {
        if (_riders.TryGetValue(userId, out var rider))
        {
            // Update stats
            rider.SpeedKmh = speedKmh;
            rider.PowerWatts = powerWatts;
            rider.HeartRate = heartRate;
            
            // Calculate distance traveled in this tick
            // Speed is km/h. m/s = speed / 3.6
            double distanceMeters = (speedKmh / 3.6) * deltaSeconds;
            
            rider.TotalDistance += distanceMeters;
            
            // Update Position
            var (x, y, rot) = RaceLogic.CalculatePosition(rider.TotalDistance);
            rider.X = x;
            rider.Y = y;
            rider.Rotation = rot;
            
            rider.LastUpdate = DateTime.Now;
            
            NotifyStateChanged();
        }
    }

    public IEnumerable<RiderState> GetRiders()
    {
        return _riders.Values;
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();
}

public class RiderState
{
    public User User { get; set; } = null!;
    public double TotalDistance { get; set; }
    public double SpeedKmh { get; set; }
    public double PowerWatts { get; set; }
    public double HeartRate { get; set; }
    
    // Visual Coordinates
    public double X { get; set; }
    public double Y { get; set; }
    public double Rotation { get; set; }
    
    public DateTime LastUpdate { get; set; }
}
