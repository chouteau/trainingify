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
    private ushort? _lastCrankRevs;
    private ushort? _lastCrankEventTime;

    private static readonly Guid FtmsServiceUuid = Guid.Parse("00001826-0000-1000-8000-00805f9b34fb");
    private static readonly Guid IndoorBikeDataUuid = Guid.Parse("00002ad2-0000-1000-8000-00805f9b34fb");

    private static readonly Guid CyclingPowerServiceUuid = Guid.Parse("00001818-0000-1000-8000-00805f9b34fb");
    private static readonly Guid CyclingPowerMeasurementUuid = Guid.Parse("00002a63-0000-1000-8000-00805f9b34fb");

    private static readonly Guid HeartRateServiceUuid = Guid.Parse("0000180d-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HeartRateMeasurementUuid = Guid.Parse("00002a37-0000-1000-8000-00805f9b34fb");
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

    // Device Settings & Connections
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
            }
        }
    }

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
    public List<DiscoveredDevice> DiscoveredDevices { get; private set; } = new();
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

        // Ensure we always have the default mock devices as backup options or if no real devices found
        lock (DiscoveredDevices)
        {
            if (protocol == "All" || protocol == "Bluetooth")
            {
                var mocks = new[]
                {
                    new DiscoveredDevice { Name = "Tacx Neo T2900", Protocol = "Bluetooth", Address = "Tacx-T2900-BLE-7548", DeviceType = "Controllable", Rssi = -62 },
                    new DiscoveredDevice { Name = "Wahoo KICKR v5", Protocol = "Bluetooth", Address = "KICKR-BLE-2894", DeviceType = "Controllable", Rssi = -71 },
                    new DiscoveredDevice { Name = "Polar H10", Protocol = "Bluetooth", Address = "Polar-H10-BLE-3948", DeviceType = "HRM", Rssi = -55 },
                    new DiscoveredDevice { Name = "Moxy Muscle O2", Protocol = "Bluetooth", Address = "Moxy-BLE-1948", DeviceType = "Moxy", Rssi = -68 },
                    new DiscoveredDevice { Name = "Core Temp Sensor", Protocol = "Bluetooth", Address = "Core-BLE-2849", DeviceType = "CoreTemp", Rssi = -60 }
                };
                foreach (var mock in mocks)
                {
                    if (!DiscoveredDevices.Any(d => d.Address == mock.Address && d.DeviceType == mock.DeviceType))
                    {
                        DiscoveredDevices.Add(mock);
                    }
                }
            }
            if (protocol == "All" || protocol == "ANT+")
            {
                var mocks = new[]
                {
                    new DiscoveredDevice { Name = "Tacx Trainer (ANT+)", Protocol = "ANT+", Address = "Tacx-T2900-ANT-48591", DeviceType = "Controllable", Rssi = 85 },
                    new DiscoveredDevice { Name = "KICKR Smart (ANT+)", Protocol = "ANT+", Address = "KICKR-ANT-10928", DeviceType = "Controllable", Rssi = 78 },
                    new DiscoveredDevice { Name = "Polar H10 (ANT+)", Protocol = "ANT+", Address = "Polar-H10-ANT-62841", DeviceType = "HRM", Rssi = 90 },
                    new DiscoveredDevice { Name = "Garmin HRM-Pro (ANT+)", Protocol = "ANT+", Address = "Garmin-HRM-ANT-93847", DeviceType = "HRM", Rssi = 82 },
                    new DiscoveredDevice { Name = "Moxy Muscle (ANT+)", Protocol = "ANT+", Address = "Moxy-ANT-49281", DeviceType = "Moxy", Rssi = 75 },
                    new DiscoveredDevice { Name = "Core Temp (ANT+)", Protocol = "ANT+", Address = "Core-ANT-74928", DeviceType = "CoreTemp", Rssi = 80 }
                };
                foreach (var mock in mocks)
                {
                    if (!DiscoveredDevices.Any(d => d.Address == mock.Address && d.DeviceType == mock.DeviceType))
                    {
                        DiscoveredDevices.Add(mock);
                    }
                }
            }
        }

        IsScanning = false;
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

        var addressStr = args.BluetoothAddress.ToString("X");
        
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
        
        NotifyStateChanged();
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
#endif

    public void Dispose()
    {
        _simulationTimer?.Dispose();
#if WINDOWS
        DisconnectControllable();
        DisconnectHrm();
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
