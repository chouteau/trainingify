using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Trainingify.Data;

namespace Trainingify.Services;

public class TrainingStateService : IDisposable
{
    private readonly IDbContextFactory<TrainingifyDbContext> _dbFactory;
    private System.Threading.Timer? _simulationTimer;
    private readonly Random _random = new();

    public event Action? OnStateChanged;

    // User Profile
    public UserProfile Profile { get; private set; } = new();

    // Workouts
    public List<Workout> AvailableWorkouts { get; private set; } = new();
    public Workout? ActiveWorkout { get; private set; }
    public List<int> WorkoutIntensityProfile { get; private set; } = new();

    // Running State
    public bool IsWorkoutActive { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public double IntervalSeconds { get; private set; }
    public int CurrentIntervalIndex { get; private set; }

    // Target Power
    public string TargetMode { get; set; } = "ERG"; // ERG, RESISTANCE, SLOPE
    public double TargetPower { get; set; } = 200;

    // Real-time Metrics
    public double Power { get; private set; }
    public double HeartRate { get; private set; }
    public double Cadence { get; private set; }
    public double Speed { get; private set; }
    public double SmO2 { get; private set; }
    public double THb { get; private set; }
    public double? CoreTemp { get; private set; }
    public double? SkinTemp { get; private set; }

    // Device Settings & Connections
    public bool ControllableConnected { get; set; }
    public bool HrmConnected { get; set; }
    public bool MoxyConnected { get; set; }
    public bool CoreTempConnected { get; set; }

    // Historical Chart Data (lasts 60 seconds)
    public List<double> PowerHistory { get; private set; } = new();
    public List<double> HeartRateHistory { get; private set; } = new();

    public TrainingStateService(IDbContextFactory<TrainingifyDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
        InitializeAsync().ConfigureAwait(false);
    }

    private async Task InitializeAsync()
    {
        using var context = await _dbFactory.CreateDbContextAsync();
        
        // Load Profile
        var profile = await context.UserProfiles.FirstOrDefaultAsync();
        if (profile != null)
        {
            Profile = profile;
            TargetPower = profile.Ftp; // default target to FTP
        }

        // Load Workouts
        AvailableWorkouts = await context.Workouts.ToListAsync();
        if (AvailableWorkouts.Any())
        {
            SelectWorkout(AvailableWorkouts.First());
        }

        NotifyStateChanged();
    }

    public async Task UpdateProfileAsync(double weight, double ftp)
    {
        Profile.Weight = weight;
        Profile.Ftp = ftp;
        TargetPower = ftp;

        using var context = await _dbFactory.CreateDbContextAsync();
        context.UserProfiles.Update(Profile);
        await context.SaveChangesAsync();
        NotifyStateChanged();
    }

    public void SelectWorkout(Workout workout)
    {
        ActiveWorkout = workout;
        ElapsedSeconds = 0;
        IntervalSeconds = 0;
        CurrentIntervalIndex = 0;
        IsWorkoutActive = false;

        // Parse intensity profile from JSON
        try
        {
            var cleanJson = workout.IntensityProfileJson.Trim('[', ']');
            if (string.IsNullOrWhiteSpace(cleanJson))
            {
                WorkoutIntensityProfile = new List<int> { 150 };
            }
            else
            {
                WorkoutIntensityProfile = cleanJson.Split(',')
                    .Select(s => int.Parse(s.Trim()))
                    .ToList();
            }
        }
        catch
        {
            WorkoutIntensityProfile = new List<int> { 150 };
        }

        if (WorkoutIntensityProfile.Any())
        {
            TargetPower = WorkoutIntensityProfile.First();
        }

        NotifyStateChanged();
    }

    public void StartWorkout()
    {
        if (IsWorkoutActive) return;

        IsWorkoutActive = true;
        _simulationTimer = new System.Threading.Timer(Tick, null, 0, 1000);
        NotifyStateChanged();
    }

    public void PauseWorkout()
    {
        if (!IsWorkoutActive) return;

        IsWorkoutActive = false;
        _simulationTimer?.Dispose();
        _simulationTimer = null;
        NotifyStateChanged();
    }

    public void SkipInterval()
    {
        if (WorkoutIntensityProfile.Count > 0)
        {
            CurrentIntervalIndex = (CurrentIntervalIndex + 1) % WorkoutIntensityProfile.Count;
            TargetPower = WorkoutIntensityProfile[CurrentIntervalIndex];
            IntervalSeconds = 0;
            NotifyStateChanged();
        }
    }

    private void Tick(object? state)
    {
        ElapsedSeconds++;
        IntervalSeconds++;

        // Simulating data updates
        SimulateMetrics();

        // Check interval transitions (e.g. change every 5 minutes in simulation, or simple progress)
        // For structured workouts, lets change interval every 60 seconds in our UI simulator so the user sees the transitions happen.
        if (IntervalSeconds >= 60) 
        {
            IntervalSeconds = 0;
            if (WorkoutIntensityProfile.Count > 0)
            {
                CurrentIntervalIndex = (CurrentIntervalIndex + 1) % WorkoutIntensityProfile.Count;
                TargetPower = WorkoutIntensityProfile[CurrentIntervalIndex];
            }
        }

        NotifyStateChanged();
    }

    private void SimulateMetrics()
    {
        // Simulate variables based on whether devices are connected/enabled
        if (ControllableConnected)
        {
            // Power fluctuates around TargetPower
            var dev = (TargetPower * 0.05); // 5% dev
            Power = Math.Max(0, Math.Round(TargetPower + (_random.NextDouble() * 2 - 1) * dev));
            Cadence = Math.Max(0, Math.Round(80 + (_random.NextDouble() * 10 - 5)));
            Speed = Math.Max(0, Math.Round((Power / 7.0) + 10 + (_random.NextDouble() * 2 - 1), 1));
        }
        else
        {
            Power = 0;
            Cadence = 0;
            Speed = 0;
        }

        if (HrmConnected)
        {
            // Heart rate rises with higher power
            var baseHr = 70.0;
            var maxHr = 190.0;
            var targetHr = baseHr + (Power / Profile.Ftp) * 110.0;
            targetHr = Math.Clamp(targetHr, baseHr, maxHr);
            
            // smooth transition
            if (HeartRate == 0) HeartRate = targetHr;
            else HeartRate = Math.Round(HeartRate + (targetHr - HeartRate) * 0.1 + (_random.NextDouble() * 2 - 1));
        }
        else
        {
            HeartRate = 0;
        }

        if (MoxyConnected)
        {
            // Muscle Oxygenation drops during higher power outputs
            var targetSmO2 = 65.0 - (Power / Profile.Ftp) * 35.0;
            targetSmO2 = Math.Clamp(targetSmO2, 30.0, 80.0);
            
            if (SmO2 == 0) SmO2 = targetSmO2;
            else SmO2 = Math.Round(SmO2 + (targetSmO2 - SmO2) * 0.05 + (_random.NextDouble() * 0.5 - 0.25), 1);

            var targetTHb = 11.0 + (Power / Profile.Ftp) * 1.5;
            if (THb == 0) THb = targetTHb;
            else THb = Math.Round(THb + (targetTHb - THb) * 0.05 + (_random.NextDouble() * 0.1 - 0.05), 2);
        }
        else
        {
            SmO2 = 0;
            THb = 0;
        }

        if (CoreTempConnected)
        {
            CoreTemp = Math.Round(37.2 + (Power / Profile.Ftp) * 1.2 + (_random.NextDouble() * 0.1), 1);
            SkinTemp = Math.Round(32.5 + (_random.NextDouble() * 0.5 - 0.25), 1);
        }
        else
        {
            CoreTemp = null;
            SkinTemp = null;
        }

        // Add to history
        PowerHistory.Add(Power);
        if (PowerHistory.Count > 60) PowerHistory.RemoveAt(0);

        HeartRateHistory.Add(HeartRate);
        if (HeartRateHistory.Count > 60) HeartRateHistory.RemoveAt(0);
    }

    private void NotifyStateChanged()
    {
        OnStateChanged?.Invoke();
    }

    // Scanning & Device management
    public List<DiscoveredDevice> DiscoveredDevices { get; private set; } = new()
    {
        // Pre-populate with some default discovered devices
        new DiscoveredDevice { Name = "Tacx Neo T2900", Protocol = "Bluetooth", Address = "Tacx-T2900-BLE-7548", DeviceType = "Controllable", Rssi = -62 },
        new DiscoveredDevice { Name = "Tacx Trainer (ANT+)", Protocol = "ANT+", Address = "Tacx-T2900-ANT-48591", DeviceType = "Controllable", Rssi = 85 },
        new DiscoveredDevice { Name = "Polar H10", Protocol = "Bluetooth", Address = "Polar-H10-BLE-3948", DeviceType = "HRM", Rssi = -55 },
        new DiscoveredDevice { Name = "Polar H10 (ANT+)", Protocol = "ANT+", Address = "Polar-H10-ANT-62841", DeviceType = "HRM", Rssi = 90 },
        new DiscoveredDevice { Name = "Moxy Muscle O2", Protocol = "Bluetooth", Address = "Moxy-BLE-1948", DeviceType = "Moxy", Rssi = -68 },
        new DiscoveredDevice { Name = "Moxy Muscle (ANT+)", Protocol = "ANT+", Address = "Moxy-ANT-49281", DeviceType = "Moxy", Rssi = 75 },
        new DiscoveredDevice { Name = "Core Temp Sensor", Protocol = "Bluetooth", Address = "Core-BLE-2849", DeviceType = "CoreTemp", Rssi = -60 },
        new DiscoveredDevice { Name = "Core Temp (ANT+)", Protocol = "ANT+", Address = "Core-ANT-74928", DeviceType = "CoreTemp", Rssi = 80 }
    };
    public bool IsScanning { get; private set; }

    public string SelectedControllableId { get; set; } = "Tacx-T2900-BLE-7548";
    public string SelectedHrmId { get; set; } = "Polar-H10-BLE-3948";
    public string SelectedMoxyId { get; set; } = "Moxy-BLE-1948";
    public string SelectedCoreTempId { get; set; } = "Core-BLE-2849";

    public string GetDeviceProtocol(string deviceType, string address)
    {
        var dev = DiscoveredDevices.FirstOrDefault(d => d.Address == address && d.DeviceType == deviceType);
        return dev?.Protocol ?? "Bluetooth";
    }

    public string GetDeviceName(string deviceType, string address)
    {
        var dev = DiscoveredDevices.FirstOrDefault(d => d.Address == address && d.DeviceType == deviceType);
        return dev?.Name ?? "Simulateur par défaut";
    }

    public async Task ScanDevicesAsync(string protocol)
    {
        if (IsScanning) return;
        IsScanning = true;
        DiscoveredDevices.Clear();
        NotifyStateChanged();

        await Task.Delay(1500); // Simulate scan latency

        if (protocol == "All" || protocol == "Bluetooth")
        {
            DiscoveredDevices.AddRange(new[]
            {
                new DiscoveredDevice { Name = "Tacx Neo T2900", Protocol = "Bluetooth", Address = "Tacx-T2900-BLE-7548", DeviceType = "Controllable", Rssi = -62 },
                new DiscoveredDevice { Name = "Wahoo KICKR v5", Protocol = "Bluetooth", Address = "KICKR-BLE-2894", DeviceType = "Controllable", Rssi = -71 },
                new DiscoveredDevice { Name = "Polar H10", Protocol = "Bluetooth", Address = "Polar-H10-BLE-3948", DeviceType = "HRM", Rssi = -55 },
                new DiscoveredDevice { Name = "Moxy Muscle O2", Protocol = "Bluetooth", Address = "Moxy-BLE-1948", DeviceType = "Moxy", Rssi = -68 },
                new DiscoveredDevice { Name = "Core Temp Sensor", Protocol = "Bluetooth", Address = "Core-BLE-2849", DeviceType = "CoreTemp", Rssi = -60 }
            });
        }
        if (protocol == "All" || protocol == "ANT+")
        {
            DiscoveredDevices.AddRange(new[]
            {
                new DiscoveredDevice { Name = "Tacx Trainer (ANT+)", Protocol = "ANT+", Address = "Tacx-T2900-ANT-48591", DeviceType = "Controllable", Rssi = 85 },
                new DiscoveredDevice { Name = "KICKR Smart (ANT+)", Protocol = "ANT+", Address = "KICKR-ANT-10928", DeviceType = "Controllable", Rssi = 78 },
                new DiscoveredDevice { Name = "Polar H10 (ANT+)", Protocol = "ANT+", Address = "Polar-H10-ANT-62841", DeviceType = "HRM", Rssi = 90 },
                new DiscoveredDevice { Name = "Garmin HRM-Pro (ANT+)", Protocol = "ANT+", Address = "Garmin-HRM-ANT-93847", DeviceType = "HRM", Rssi = 82 },
                new DiscoveredDevice { Name = "Moxy Muscle (ANT+)", Protocol = "ANT+", Address = "Moxy-ANT-49281", DeviceType = "Moxy", Rssi = 75 },
                new DiscoveredDevice { Name = "Core Temp (ANT+)", Protocol = "ANT+", Address = "Core-ANT-74928", DeviceType = "CoreTemp", Rssi = 80 }
            });
        }

        IsScanning = false;
        NotifyStateChanged();
    }

    public void Dispose()
    {
        _simulationTimer?.Dispose();
    }
}

public class DiscoveredDevice
{
    public string Name { get; set; } = null!;
    public string Protocol { get; set; } = null!; // Bluetooth, ANT+
    public string Address { get; set; } = null!;
    public string DeviceType { get; set; } = null!; // Controllable, HRM, Moxy, CoreTemp
    public int Rssi { get; set; }
}
