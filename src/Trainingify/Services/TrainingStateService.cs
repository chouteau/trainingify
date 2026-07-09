using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Trainingify.Data;

#if WINDOWS
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using Windows.Devices.Enumeration;
#endif

namespace Trainingify.Services;

public class TrainingStateService : IDisposable
{
    private readonly IDbContextFactory<TrainingifyDbContext> _dbFactory;
    private System.Threading.Timer? _simulationTimer;
    private readonly Random _random = new();

#if WINDOWS
    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _controllableDevice;
    private GattCharacteristic? _ftmsCharacteristic;
    private BluetoothLEDevice? _hrmDevice;
    private GattCharacteristic? _hrmCharacteristic;
    private BluetoothLEDevice? _fanDevice;
    private GattCharacteristic? _fanCharacteristic;
    private ushort? _lastCrankRevs;
    private ushort? _lastCrankEventTime;

    private static readonly Guid FtmsServiceUuid = Guid.Parse("00001826-0000-1000-8000-00805f9b34fb");
    private static readonly Guid IndoorBikeDataUuid = Guid.Parse("00002ad2-0000-1000-8000-00805f9b34fb");

    private static readonly Guid CyclingPowerServiceUuid = Guid.Parse("00001818-0000-1000-8000-00805f9b34fb");
    private static readonly Guid CyclingPowerMeasurementUuid = Guid.Parse("00002a63-0000-1000-8000-00805f9b34fb");

    private static readonly Guid HeartRateServiceUuid = Guid.Parse("0000180d-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HeartRateMeasurementUuid = Guid.Parse("00002a37-0000-1000-8000-00805f9b34fb");

    private static readonly Guid FanServiceUuid = Guid.Parse("a026e037-0a7d-4ab3-97fa-f1500f9feb8b");
    private static readonly Guid FanControlUuid = Guid.Parse("a026e038-0a7d-4ab3-97fa-f1500f9feb8b");
#endif

    public event Action? OnStateChanged;

    public string ControllableConnectionStatus { get; private set; } = "Déconnecté";
    public string HrmConnectionStatus { get; private set; } = "Déconnecté";

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

    // Fan State
    private bool _isFanOn = true;
    public bool IsFanOn
    {
        get => _isFanOn;
        set
        {
            if (_isFanOn != value)
            {
                _isFanOn = value;
                NotifyStateChanged();
#if WINDOWS
                if (FanConnected)
                {
                    if (value)
                    {
                        _ = WriteFanModeAsync(FanMode);
                        _ = WriteFanSpeedAsync(FanSpeed);
                    }
                    else
                    {
                        _ = WriteFanSpeedAsync(0);
                    }
                }
#endif
            }
        }
    }

    private int _fanSpeed = 1;
    public int FanSpeed
    {
        get => _fanSpeed;
        set
        {
            if (_fanSpeed != value)
            {
                _fanSpeed = value;
                NotifyStateChanged();
#if WINDOWS
                if (FanConnected && IsFanOn)
                {
                    _ = WriteFanSpeedAsync(value);
                }
#endif
            }
        }
    }

    private string _fanMode = "Manual";
    public string FanMode
    {
        get => _fanMode;
        set
        {
            if (_fanMode != value)
            {
                _fanMode = value;
                NotifyStateChanged();
#if WINDOWS
                if (FanConnected && IsFanOn)
                {
                    _ = WriteFanModeAsync(value);
                }
#endif
            }
        }
    }

    // Device Settings & Connections
    private bool _fanConnected;
    public bool FanConnected
    {
        get => _fanConnected;
        set
        {
            System.Diagnostics.Debug.WriteLine($"[Fan BLE] FanConnected setter called with value: {value} (current backing field: {_fanConnected}, SelectedFanId: '{SelectedFanId}')");
            if (_fanConnected != value)
            {
                _fanConnected = value;
#if WINDOWS
                if (value) ConnectFanAsync();
                else DisconnectFan();
#endif
                NotifyStateChanged();
                _ = SaveDeviceConnectionStateAsync("Fan", SelectedFanId, value);
            }
        }
    }

    private bool _controllableConnected;
    public bool ControllableConnected
    {
        get => _controllableConnected;
        set
        {
            if (_controllableConnected != value)
            {
                _controllableConnected = value;
#if WINDOWS
                if (value) ConnectControllableAsync();
                else DisconnectControllable();
#endif
                NotifyStateChanged();
                _ = SaveDeviceConnectionStateAsync("Controllable", SelectedControllableId, value);
            }
        }
    }

    private bool _hrmConnected;
    public bool HrmConnected
    {
        get => _hrmConnected;
        set
        {
            if (_hrmConnected != value)
            {
                _hrmConnected = value;
#if WINDOWS
                if (value) ConnectHrmAsync();
                else DisconnectHrm();
#endif
                NotifyStateChanged();
                _ = SaveDeviceConnectionStateAsync("HRM", SelectedHrmId, value);
            }
        }
    }

    private bool _moxyConnected;
    public bool MoxyConnected
    {
        get => _moxyConnected;
        set
        {
            if (_moxyConnected != value)
            {
                _moxyConnected = value;
                NotifyStateChanged();
                _ = SaveDeviceConnectionStateAsync("Moxy", SelectedMoxyId, value);
            }
        }
    }

    private bool _coreTempConnected;
    public bool CoreTempConnected
    {
        get => _coreTempConnected;
        set
        {
            if (_coreTempConnected != value)
            {
                _coreTempConnected = value;
                NotifyStateChanged();
                _ = SaveDeviceConnectionStateAsync("CoreTemp", SelectedCoreTempId, value);
            }
        }
    }

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

        // Load saved devices and attempt to reconnect them
        try
        {
            var savedDevices = await context.ConnectedDevices.ToListAsync();
            foreach (var device in savedDevices)
            {
                if (string.IsNullOrEmpty(device.Address)) continue;

                // Ensure the device is in the DiscoveredDevices list so the UI displays its name properly.
                if (!DiscoveredDevices.Any(d => d.Address == device.Address && d.DeviceType == device.DeviceType))
                {
                    DiscoveredDevices.Add(new DiscoveredDevice
                    {
                        Name = device.Name,
                        Address = device.Address,
                        DeviceType = device.DeviceType,
                        Protocol = "Bluetooth",
                        Rssi = -60
                    });
                }

                // Restore selections and attempt to reconnect if they were last enabled
                switch (device.DeviceType)
                {
                    case "Controllable":
                        _selectedControllableId = device.Address;
                        ControllableConnected = device.IsEnabled;
                        break;
                    case "HRM":
                        _selectedHrmId = device.Address;
                        HrmConnected = device.IsEnabled;
                        break;
                    case "Moxy":
                        _selectedMoxyId = device.Address;
                        MoxyConnected = device.IsEnabled;
                        break;
                    case "CoreTemp":
                        _selectedCoreTempId = device.Address;
                        CoreTempConnected = device.IsEnabled;
                        break;
                    case "Fan":
                        _selectedFanId = device.Address;
                        FanConnected = device.IsEnabled;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading saved devices: {ex.Message}");
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
        bool isRealControllable = false;
#if WINDOWS
        if (_ftmsCharacteristic != null) isRealControllable = true;
#endif

        // Simulate variables based on whether devices are connected/enabled
        if (ControllableConnected && !isRealControllable)
        {
            // Power fluctuates around TargetPower
            var dev = (TargetPower * 0.05); // 5% dev
            Power = Math.Max(0, Math.Round(TargetPower + (_random.NextDouble() * 2 - 1) * dev));
            Cadence = Math.Max(0, Math.Round(80 + (_random.NextDouble() * 10 - 5)));
            Speed = Math.Max(0, Math.Round((Power / 7.0) + 10 + (_random.NextDouble() * 2 - 1), 1));
        }
        else if (!ControllableConnected)
        {
            Power = 0;
            Cadence = 0;
            Speed = 0;
        }

        bool isRealHrm = false;
#if WINDOWS
        if (_hrmCharacteristic != null) isRealHrm = true;
#endif

        if (HrmConnected && !isRealHrm)
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
        else if (!HrmConnected)
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

        if (FanConnected && IsFanOn)
        {
            if (FanMode == "TrainerSpeed")
            {
                // Speed ranges from 0 to 50+ km/h. Map to 1-5.
                FanSpeed = Speed switch
                {
                    < 10 => 1,
                    < 20 => 2,
                    < 30 => 3,
                    < 40 => 4,
                    _ => 5
                };
            }
            else if (FanMode == "HeartRate")
            {
                // HeartRate ranges from 70 to 190. Map to 1-5.
                FanSpeed = HeartRate switch
                {
                    < 100 => 1,
                    < 120 => 2,
                    < 140 => 3,
                    < 160 => 4,
                    _ => 5
                };
            }
        }
        else if (!IsFanOn)
        {
            FanSpeed = 0;
        }

        // Add to history
        lock (PowerHistory)
        {
            PowerHistory.Add(Power);
            if (PowerHistory.Count > 60) PowerHistory.RemoveAt(0);
        }

        lock (HeartRateHistory)
        {
            HeartRateHistory.Add(HeartRate);
            if (HeartRateHistory.Count > 60) HeartRateHistory.RemoveAt(0);
        }
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
        new DiscoveredDevice { Name = "Core Temp (ANT+)", Protocol = "ANT+", Address = "Core-ANT-74928", DeviceType = "CoreTemp", Rssi = 80 },
        new DiscoveredDevice { Name = "Wahoo Headwind", Protocol = "Bluetooth", Address = "Wahoo-Headwind-BLE-1849", DeviceType = "Fan", Rssi = -58 },
        new DiscoveredDevice { Name = "Wahoo Headwind (ANT+)", Protocol = "ANT+", Address = "Wahoo-Headwind-ANT-3948", DeviceType = "Fan", Rssi = 82 }
    };
    public bool IsScanning { get; private set; }

    private string _selectedControllableId = "Tacx-T2900-BLE-7548";
    public string SelectedControllableId
    {
        get => _selectedControllableId;
        set
        {
            if (_selectedControllableId != value)
            {
                _selectedControllableId = value;
                NotifyStateChanged();
#if WINDOWS
                if (ControllableConnected)
                {
                    DisconnectControllable();
                    ConnectControllableAsync();
                }
#endif
                if (ControllableConnected)
                {
                    _ = SaveDeviceConnectionStateAsync("Controllable", value, true);
                }
            }
        }
    }

    private string _selectedHrmId = "Polar-H10-BLE-3948";
    public string SelectedHrmId
    {
        get => _selectedHrmId;
        set
        {
            if (_selectedHrmId != value)
            {
                _selectedHrmId = value;
                NotifyStateChanged();
#if WINDOWS
                if (HrmConnected)
                {
                    DisconnectHrm();
                    ConnectHrmAsync();
                }
#endif
                if (HrmConnected)
                {
                    _ = SaveDeviceConnectionStateAsync("HRM", value, true);
                }
            }
        }
    }

    private string _selectedMoxyId = "Moxy-BLE-1948";
    public string SelectedMoxyId
    {
        get => _selectedMoxyId;
        set
        {
            if (_selectedMoxyId != value)
            {
                _selectedMoxyId = value;
                NotifyStateChanged();
                if (MoxyConnected)
                {
                    _ = SaveDeviceConnectionStateAsync("Moxy", value, true);
                }
            }
        }
    }

    private string _selectedCoreTempId = "Core-BLE-2849";
    public string SelectedCoreTempId
    {
        get => _selectedCoreTempId;
        set
        {
            if (_selectedCoreTempId != value)
            {
                _selectedCoreTempId = value;
                NotifyStateChanged();
                if (CoreTempConnected)
                {
                    _ = SaveDeviceConnectionStateAsync("CoreTemp", value, true);
                }
            }
        }
    }

    private string _selectedFanId = "Wahoo-Headwind-BLE-1849";
    public string SelectedFanId
    {
        get => _selectedFanId;
        set
        {
            System.Diagnostics.Debug.WriteLine($"[Fan BLE] SelectedFanId setter called with value: '{value}' (current backing field: '{_selectedFanId}', FanConnected: {FanConnected})");
            if (_selectedFanId != value)
            {
                _selectedFanId = value;
                NotifyStateChanged();
#if WINDOWS
                if (FanConnected)
                {
                    DisconnectFan();
                    ConnectFanAsync();
                }
#endif
                if (FanConnected)
                {
                    _ = SaveDeviceConnectionStateAsync("Fan", value, true);
                }
            }
        }
    }

    private async Task SaveDeviceConnectionStateAsync(string deviceType, string address, bool isConnected)
    {
        try
        {
            using var context = await _dbFactory.CreateDbContextAsync();
            var device = await context.ConnectedDevices
                .FirstOrDefaultAsync(d => d.DeviceType == deviceType);

            var name = GetDeviceName(deviceType, address);

            if (device == null)
            {
                device = new ConnectedDevice
                {
                    DeviceType = deviceType,
                    Address = address,
                    Name = name,
                    IsEnabled = isConnected
                };
                context.ConnectedDevices.Add(device);
            }
            else
            {
                device.Address = address;
                device.Name = name;
                device.IsEnabled = isConnected;
                context.ConnectedDevices.Update(device);
            }

            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving device connection state: {ex.Message}");
        }
    }

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

#if WINDOWS
        try
        {
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            _watcher.Received += OnAdvertisementReceived;
            _watcher.Start();

            // Scan for 5 seconds
            await Task.Delay(5000);

            _watcher.Stop();
            _watcher.Received -= OnAdvertisementReceived;
            _watcher = null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Scanning failed: {ex.Message}");
        }
#else
        await Task.Delay(1500); // Simulate scan latency
#endif

        if (protocol == "All" || protocol == "Bluetooth")
        {
            DiscoveredDevices.AddRange(new[]
            {
                new DiscoveredDevice { Name = "Tacx Neo T2900", Protocol = "Bluetooth", Address = "Tacx-T2900-BLE-7548", DeviceType = "Controllable", Rssi = -62 },
                new DiscoveredDevice { Name = "Wahoo KICKR v5", Protocol = "Bluetooth", Address = "KICKR-BLE-2894", DeviceType = "Controllable", Rssi = -71 },
                new DiscoveredDevice { Name = "Polar H10", Protocol = "Bluetooth", Address = "Polar-H10-BLE-3948", DeviceType = "HRM", Rssi = -55 },
                new DiscoveredDevice { Name = "Moxy Muscle O2", Protocol = "Bluetooth", Address = "Moxy-BLE-1948", DeviceType = "Moxy", Rssi = -68 },
                new DiscoveredDevice { Name = "Core Temp Sensor", Protocol = "Bluetooth", Address = "Core-BLE-2849", DeviceType = "CoreTemp", Rssi = -60 },
                new DiscoveredDevice { Name = "Wahoo Headwind", Protocol = "Bluetooth", Address = "Wahoo-Headwind-BLE-1849", DeviceType = "Fan", Rssi = -58 }
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
                new DiscoveredDevice { Name = "Core Temp (ANT+)", Protocol = "ANT+", Address = "Core-ANT-74928", DeviceType = "CoreTemp", Rssi = 80 },
                new DiscoveredDevice { Name = "Wahoo Headwind (ANT+)", Protocol = "ANT+", Address = "Wahoo-Headwind-ANT-3948", DeviceType = "Fan", Rssi = 82 }
            });
        }
        
        NotifyStateChanged();
    }

#if WINDOWS
    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var localName = args.Advertisement.LocalName;
        if (string.IsNullOrEmpty(localName)) return;

        string deviceType = "Controllable"; // Default
        if (localName.Contains("KICKR", StringComparison.OrdinalIgnoreCase) || 
            localName.Contains("Tacx", StringComparison.OrdinalIgnoreCase) || 
            localName.Contains("Trainer", StringComparison.OrdinalIgnoreCase) ||
            localName.Contains("Bike", StringComparison.OrdinalIgnoreCase))
        {
            deviceType = "Controllable";
        }
        else if (localName.Contains("Polar", StringComparison.OrdinalIgnoreCase) || 
                 localName.Contains("HRM", StringComparison.OrdinalIgnoreCase) || 
                 localName.Contains("H10", StringComparison.OrdinalIgnoreCase) ||
                 localName.Contains("Heart", StringComparison.OrdinalIgnoreCase))
        {
            deviceType = "HRM";
        }
        else if (localName.Contains("Moxy", StringComparison.OrdinalIgnoreCase))
        {
            deviceType = "Moxy";
        }
        else if (localName.Contains("Core", StringComparison.OrdinalIgnoreCase))
        {
            deviceType = "CoreTemp";
        }
        else if (localName.Contains("Headwind", StringComparison.OrdinalIgnoreCase) ||
                 localName.Contains("Fan", StringComparison.OrdinalIgnoreCase))
        {
            deviceType = "Fan";
        }

        var addressStr = args.BluetoothAddress.ToString("X");
        System.Diagnostics.Debug.WriteLine($"[BLE Scan] Found device: '{localName}' ({addressStr}) - Type: {deviceType}");
        
        lock (DiscoveredDevices)
        {
            if (DiscoveredDevices.Any(d => d.Address == addressStr && d.DeviceType == deviceType))
                return;

            DiscoveredDevices.Add(new DiscoveredDevice
            {
                Name = localName,
                Protocol = "Bluetooth",
                Address = addressStr,
                DeviceType = deviceType,
                Rssi = args.RawSignalStrengthInDBm
            });
        }
    }

    private async void ConnectControllableAsync()
    {
        if (string.IsNullOrEmpty(SelectedControllableId)) return;

        ControllableConnectionStatus = "Connexion en cours...";
        NotifyStateChanged();

        try
        {
            if (!ulong.TryParse(SelectedControllableId, System.Globalization.NumberStyles.HexNumber, null, out ulong address))
            {
                ControllableConnectionStatus = "Adresse invalide (Simulateur)";
                NotifyStateChanged();
                return;
            }

            _controllableDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_controllableDevice == null)
            {
                ControllableConnectionStatus = "Appareil introuvable";
                NotifyStateChanged();
                return;
            }

            ControllableConnectionStatus = "Recherche des services...";
            NotifyStateChanged();

            var servicesResult = await _controllableDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                ControllableConnectionStatus = $"Échec services: {servicesResult.Status}";
                NotifyStateChanged();
                return;
            }

            // 1. Try FTMS first
            var ftmsService = servicesResult.Services.FirstOrDefault(s => s.Uuid == FtmsServiceUuid);
            if (ftmsService != null)
            {
                ControllableConnectionStatus = "Connexion FTMS...";
                NotifyStateChanged();

                var charResult = await ftmsService.GetCharacteristicsForUuidAsync(IndoorBikeDataUuid);
                if (charResult.Status == GattCommunicationStatus.Success && charResult.Characteristics.Count > 0)
                {
                    _ftmsCharacteristic = charResult.Characteristics[0];
                    _ftmsCharacteristic.ValueChanged += OnFtmsValueChanged;
                    var status = await _ftmsCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    
                    if (status == GattCommunicationStatus.Success)
                    {
                        ControllableConnectionStatus = "Connecté (FTMS)";
                    }
                    else
                    {
                        ControllableConnectionStatus = $"Échec notifications FTMS: {status}";
                    }
                    NotifyStateChanged();
                    return;
                }
            }

            // 2. Try Cycling Power Service as fallback
            var cpsService = servicesResult.Services.FirstOrDefault(s => s.Uuid == CyclingPowerServiceUuid);
            if (cpsService != null)
            {
                ControllableConnectionStatus = "Connexion Cycling Power...";
                NotifyStateChanged();

                var charResult = await cpsService.GetCharacteristicsForUuidAsync(CyclingPowerMeasurementUuid);
                if (charResult.Status == GattCommunicationStatus.Success && charResult.Characteristics.Count > 0)
                {
                    _ftmsCharacteristic = charResult.Characteristics[0];
                    _ftmsCharacteristic.ValueChanged += OnCpsValueChanged;
                    var status = await _ftmsCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    
                    if (status == GattCommunicationStatus.Success)
                    {
                        ControllableConnectionStatus = "Connecté (Cycling Power)";
                    }
                    else
                    {
                        ControllableConnectionStatus = $"Échec notifications CPS: {status}";
                    }
                    NotifyStateChanged();
                    return;
                }
            }

            ControllableConnectionStatus = "Services FTMS/CPS introuvables";
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            ControllableConnectionStatus = $"Erreur: {ex.Message}";
            NotifyStateChanged();
        }
    }

    private void DisconnectControllable()
    {
        try
        {
            if (_ftmsCharacteristic != null)
            {
                _ftmsCharacteristic.ValueChanged -= OnFtmsValueChanged;
                _ftmsCharacteristic.ValueChanged -= OnCpsValueChanged;
                _ = _ftmsCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
                _ftmsCharacteristic = null;
            }
            _controllableDevice?.Dispose();
            _controllableDevice = null;
        }
        catch {}
        ControllableConnectionStatus = "Déconnecté";
    }

    private void OnFtmsValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (args.CharacteristicValue.Length < 2) return;

        try
        {
            var logReader = DataReader.FromBuffer(args.CharacteristicValue);
            logReader.ByteOrder = ByteOrder.LittleEndian;
            var data = new byte[args.CharacteristicValue.Length];
            logReader.ReadBytes(data);
            string hex = BitConverter.ToString(data);
            ControllableConnectionStatus = $"FTMS ({args.CharacteristicValue.Length}B): {hex}";
        }
        catch {}

        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        reader.ByteOrder = ByteOrder.LittleEndian;
        ushort flags = reader.ReadUInt16();

        double speedVal = 0;
        double cadenceVal = 0;
        double powerVal = 0;

        // Bit 0: More Data (0 = Instantaneous Speed present)
        if ((flags & (1 << 0)) == 0)
        {
            if (reader.UnconsumedBufferLength >= 2)
            {
                speedVal = reader.ReadUInt16() * 0.01;
            }
        }

        // Bit 1: Average Speed present
        if ((flags & (1 << 1)) != 0)
        {
            if (reader.UnconsumedBufferLength >= 2) reader.ReadUInt16();
        }

        // Bit 2: Instantaneous Cadence present (0.5 rpm)
        if ((flags & (1 << 2)) != 0)
        {
            if (reader.UnconsumedBufferLength >= 2)
            {
                cadenceVal = reader.ReadUInt16() * 0.5;
            }
        }

        // Bit 3: Average Cadence present
        if ((flags & (1 << 3)) != 0)
        {
            if (reader.UnconsumedBufferLength >= 2) reader.ReadUInt16();
        }

        // Bit 4: Total Distance present (uint24)
        if ((flags & (1 << 4)) != 0)
        {
            if (reader.UnconsumedBufferLength >= 3)
			{
				reader.ReadByte();
				reader.ReadByte();
				reader.ReadByte();
			}
        }

        // Bit 5: Resistance Level present
        if ((flags & (1 << 5)) != 0)
        {
            if (reader.UnconsumedBufferLength >= 2)
            {
                reader.ReadInt16();
            }
        }

        // Bit 6: Instantaneous Power present (1 W)
        if ((flags & (1 << 6)) != 0)
        {
            if (reader.UnconsumedBufferLength >= 2)
            {
                powerVal = reader.ReadInt16();
            }
        }

        Power = powerVal;
        Cadence = cadenceVal;
        Speed = speedVal;

        lock (PowerHistory)
        {
            PowerHistory.Add(Power);
            if (PowerHistory.Count > 60) PowerHistory.RemoveAt(0);
        }

        NotifyStateChanged();
    }

    private void OnCpsValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (args.CharacteristicValue.Length < 4) return;

        try
        {
            var logReader = DataReader.FromBuffer(args.CharacteristicValue);
            logReader.ByteOrder = ByteOrder.LittleEndian;
            var data = new byte[args.CharacteristicValue.Length];
            logReader.ReadBytes(data);
            string hex = BitConverter.ToString(data);
            ControllableConnectionStatus = $"CPS ({args.CharacteristicValue.Length}B): {hex}";
        }
        catch {}

        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        reader.ByteOrder = ByteOrder.LittleEndian;
        ushort flags = reader.ReadUInt16();
        double powerVal = reader.ReadInt16();

        double cadenceVal = 0;

        if ((flags & (1 << 0)) != 0)
        {
            if (reader.UnconsumedBufferLength >= 1) reader.ReadByte();
        }
        if ((flags & (1 << 2)) != 0)
        {
            if (reader.UnconsumedBufferLength >= 4) reader.ReadUInt32();
        }
        if ((flags & (1 << 4)) != 0)
        {
            if (reader.UnconsumedBufferLength >= 6)
            {
                reader.ReadUInt32();
                reader.ReadUInt16();
            }
        }
        if ((flags & (1 << 5)) != 0)
        {
            if (reader.UnconsumedBufferLength >= 4)
            {
                ushort cumulativeCrankRevs = reader.ReadUInt16();
                ushort lastCrankEventTime = reader.ReadUInt16();

                if (_lastCrankRevs.HasValue && _lastCrankEventTime.HasValue)
                {
                    int revsDiff = cumulativeCrankRevs - _lastCrankRevs.Value;
                    if (revsDiff < 0) revsDiff += 65536;

                    int timeDiff = lastCrankEventTime - _lastCrankEventTime.Value;
                    if (timeDiff < 0) timeDiff += 65536;

                    if (timeDiff > 0 && revsDiff > 0)
                    {
                        double minutes = (timeDiff / 1024.0) / 60.0;
                        cadenceVal = Math.Round(revsDiff / minutes);
                    }
                }
                _lastCrankRevs = cumulativeCrankRevs;
                _lastCrankEventTime = lastCrankEventTime;
            }
        }

        Power = powerVal;
        Cadence = cadenceVal;

        lock (PowerHistory)
        {
            PowerHistory.Add(Power);
            if (PowerHistory.Count > 60) PowerHistory.RemoveAt(0);
        }

        NotifyStateChanged();
    }

    private async void ConnectHrmAsync()
    {
        if (string.IsNullOrEmpty(SelectedHrmId)) return;

        HrmConnectionStatus = "Connexion en cours...";
        NotifyStateChanged();

        try
        {
            if (!ulong.TryParse(SelectedHrmId, System.Globalization.NumberStyles.HexNumber, null, out ulong address))
            {
                HrmConnectionStatus = "Adresse invalide (Simulateur)";
                NotifyStateChanged();
                return;
            }

            _hrmDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_hrmDevice == null)
            {
                HrmConnectionStatus = "Appareil introuvable";
                NotifyStateChanged();
                return;
            }

            HrmConnectionStatus = "Recherche des services...";
            NotifyStateChanged();

            var servicesResult = await _hrmDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                HrmConnectionStatus = $"Échec services: {servicesResult.Status}";
                NotifyStateChanged();
                return;
            }

            var hrmService = servicesResult.Services.FirstOrDefault(s => s.Uuid == HeartRateServiceUuid);
            if (hrmService != null)
            {
                HrmConnectionStatus = "Connexion HRM...";
                NotifyStateChanged();

                var charResult = await hrmService.GetCharacteristicsForUuidAsync(HeartRateMeasurementUuid);
                if (charResult.Status == GattCommunicationStatus.Success && charResult.Characteristics.Count > 0)
                {
                    _hrmCharacteristic = charResult.Characteristics[0];
                    _hrmCharacteristic.ValueChanged += OnHrmValueChanged;
                    var status = await _hrmCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    
                    if (status == GattCommunicationStatus.Success)
                    {
                        HrmConnectionStatus = "Connecté (HRM)";
                    }
                    else
                    {
                        HrmConnectionStatus = $"Échec notifications HRM: {status}";
                    }
                    NotifyStateChanged();
                    return;
                }
            }

            HrmConnectionStatus = "Service de fréquence cardiaque introuvable";
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            HrmConnectionStatus = $"Erreur: {ex.Message}";
            NotifyStateChanged();
        }
    }

    private void DisconnectHrm()
    {
        try
        {
            if (_hrmCharacteristic != null)
            {
                _hrmCharacteristic.ValueChanged -= OnHrmValueChanged;
                _ = _hrmCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
                _hrmCharacteristic = null;
            }
            _hrmDevice?.Dispose();
            _hrmDevice = null;
        }
        catch {}
        HrmConnectionStatus = "Déconnecté";
    }

    private void OnHrmValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (args.CharacteristicValue.Length < 2) return;

        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        reader.ByteOrder = ByteOrder.LittleEndian;
        byte flags = reader.ReadByte();

        double hrVal = 0;
        if ((flags & 0x01) == 0)
        {
            hrVal = reader.ReadByte();
        }
        else
        {
            if (reader.UnconsumedBufferLength >= 2)
            {
                hrVal = reader.ReadUInt16();
            }
        }

        HeartRate = hrVal;

        lock (HeartRateHistory)
        {
            HeartRateHistory.Add(HeartRate);
            if (HeartRateHistory.Count > 60) HeartRateHistory.RemoveAt(0);
        }

        NotifyStateChanged();
    }

    private async void ConnectFanAsync()
    {
        System.Diagnostics.Debug.WriteLine($"[Fan BLE] ConnectFanAsync called for SelectedFanId: '{SelectedFanId}'");
        if (string.IsNullOrEmpty(SelectedFanId))
        {
            System.Diagnostics.Debug.WriteLine($"[Fan BLE] SelectedFanId is null or empty");
            return;
        }

        try
        {
            if (!ulong.TryParse(SelectedFanId, System.Globalization.NumberStyles.HexNumber, null, out ulong address))
            {
                System.Diagnostics.Debug.WriteLine($"[Fan BLE] SelectedFanId '{SelectedFanId}' is not a valid hex ulong address");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[Fan BLE] Connecting to address {address:X}...");
            _fanDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_fanDevice == null)
            {
                System.Diagnostics.Debug.WriteLine($"[Fan BLE] FromBluetoothAddressAsync returned null device");
                return;
            }

            // Check and attempt pairing if needed
            if (!_fanDevice.DeviceInformation.Pairing.IsPaired)
            {
                System.Diagnostics.Debug.WriteLine("[Fan BLE] Device is not paired in Windows. Attempting programmatic pairing...");
                var pairingResult = await _fanDevice.DeviceInformation.Pairing.PairAsync();
                System.Diagnostics.Debug.WriteLine($"[Fan BLE] Programmatic pairing result status: {pairingResult.Status}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Fan BLE] Device is already paired in Windows.");
            }

            System.Diagnostics.Debug.WriteLine($"[Fan BLE] Device connected. Querying services...");
            var servicesResult = await _fanDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            System.Diagnostics.Debug.WriteLine($"[Fan BLE] GetGattServicesAsync status: {servicesResult.Status}");
            if (servicesResult.Status != GattCommunicationStatus.Success) return;

            GattCharacteristic? foundChar = null;

            foreach (var service in servicesResult.Services)
            {
                System.Diagnostics.Debug.WriteLine($"[Fan BLE] Service found: {service.Uuid}");
                if (service.Uuid.ToString().StartsWith("a026", StringComparison.OrdinalIgnoreCase))
                {
                    var charResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    System.Diagnostics.Debug.WriteLine($"[Fan BLE]   GetCharacteristicsAsync for service {service.Uuid} status: {charResult.Status}");
                    if (charResult.Status == GattCommunicationStatus.Success)
                    {
                        foreach (var c in charResult.Characteristics)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Fan BLE]   Characteristic found under {service.Uuid}: {c.Uuid} (Properties: {c.CharacteristicProperties})");
                            
                            // Check if this characteristic is writable
                            if (foundChar == null && 
                                (c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) || 
                                 c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)))
                            {
                                foundChar = c;
                                System.Diagnostics.Debug.WriteLine($"[Fan BLE]   Selected as candidate control characteristic: {c.Uuid}");
                            }
                        }
                    }
                }
            }

            if (foundChar != null)
            {
                _fanCharacteristic = foundChar;
                System.Diagnostics.Debug.WriteLine($"[Fan BLE] Characteristic {_fanCharacteristic.Uuid} selected for control!");
                
                // Once connected, write initial mode and speed
                await WriteFanModeAsync(FanMode);
                if (IsFanOn)
                {
                    await WriteFanSpeedAsync(FanSpeed);
                }
                else
                {
                    await WriteFanSpeedAsync(0);
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Fan BLE] No writable characteristic found under any a026* service.");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Fan BLE] Error connecting to fan: {ex.Message}");
        }
    }

    private void DisconnectFan()
    {
        System.Diagnostics.Debug.WriteLine($"[Fan BLE] DisconnectFan called");
        try
        {
            if (_fanCharacteristic != null)
            {
                _fanCharacteristic = null;
            }
            _fanDevice?.Dispose();
            _fanDevice = null;
            System.Diagnostics.Debug.WriteLine($"[Fan BLE] Fan disconnected and resources disposed");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Fan BLE] Error disconnecting: {ex.Message}");
        }
    }

    private async Task WriteFanModeAsync(string mode)
    {
        System.Diagnostics.Debug.WriteLine($"[Fan BLE] WriteFanModeAsync called with mode: '{mode}'");
        if (_fanCharacteristic == null)
        {
            System.Diagnostics.Debug.WriteLine($"[Fan BLE] Cannot write mode. _fanCharacteristic is null (fan not connected)");
            return;
        }

        try
        {
            byte[] value = mode switch
            {
                "HeartRate" => new byte[] { 0x04, 0x02 },
                "TrainerSpeed" => new byte[] { 0x04, 0x03 },
                _ => new byte[] { 0x04, 0x04 } // "Manual"
            };

            System.Diagnostics.Debug.WriteLine($"[Fan BLE] Writing mode command bytes: {BitConverter.ToString(value)} to characteristic...");
            var writer = new DataWriter();
            writer.WriteBytes(value);
            var result = await _fanCharacteristic.WriteValueAsync(writer.DetachBuffer());
            System.Diagnostics.Debug.WriteLine($"[Fan BLE] Mode write operation completed with result: {result}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Fan BLE] Error writing fan mode: {ex.Message}");
        }
    }

    private async Task WriteFanSpeedAsync(int speedLevel)
    {
        System.Diagnostics.Debug.WriteLine($"[Fan BLE] WriteFanSpeedAsync called with speedLevel: {speedLevel}");
        if (_fanCharacteristic == null)
        {
            System.Diagnostics.Debug.WriteLine($"[Fan BLE] Cannot write speed. _fanCharacteristic is null (fan not connected)");
            return;
        }

        try
        {
            byte speedPercentage = speedLevel switch
            {
                0 => 0,
                1 => 20,
                2 => 40,
                3 => 60,
                4 => 80,
                5 => 100,
                _ => 0
            };

            byte[] value = new byte[] { 0x02, speedPercentage };

            System.Diagnostics.Debug.WriteLine($"[Fan BLE] Writing speed command bytes: {BitConverter.ToString(value)} to characteristic (level {speedLevel} -> {speedPercentage}%)...");
            var writer = new DataWriter();
            writer.WriteBytes(value);
            var result = await _fanCharacteristic.WriteValueAsync(writer.DetachBuffer());
            System.Diagnostics.Debug.WriteLine($"[Fan BLE] Speed write operation completed with result: {result}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Fan BLE] Error writing fan speed: {ex.Message}");
        }
    }
#endif

    public void Dispose()
    {
        _simulationTimer?.Dispose();
#if WINDOWS
        DisconnectControllable();
        DisconnectHrm();
        DisconnectFan();
#endif
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
