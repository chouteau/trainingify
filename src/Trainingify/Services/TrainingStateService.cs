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
using System.Runtime.InteropServices.WindowsRuntime;
#endif

namespace Trainingify.Services;

public class TrainingStateService : IDisposable
{
    private readonly IDbContextFactory<TrainingifyDbContext> _dbFactory;
    private static readonly object FanLogLock = new();
    public static string FanLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Trainingify", "logs", "fan-bluetooth.log");
    private System.Threading.Timer? _simulationTimer;
    private readonly Random _random = new();

#if WINDOWS
    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _controllableDevice;
    private GattCharacteristic? _ftmsCharacteristic;
    private GattCharacteristic? _controlPointCharacteristic;
    private GattCharacteristic? _wahooButtonsCharacteristic;
    private BluetoothLEDevice? _hrmDevice;
    private GattCharacteristic? _hrmCharacteristic;
    private BluetoothLEDevice? _fanDevice;
    private GattCharacteristic? _fanCharacteristic;
    private readonly SemaphoreSlim _fanWriteSemaphore = new SemaphoreSlim(1, 1);
    private TaskCompletionSource<byte[]>? _pendingFanAcknowledgement;
    private byte _pendingFanCommand;
    private byte _pendingFanValue;
    private readonly SemaphoreSlim _trainerWriteSemaphore = new SemaphoreSlim(1, 1);
    private ushort? _lastCrankRevs;
    private ushort? _lastCrankEventTime;

    private static readonly Guid FtmsServiceUuid = Guid.Parse("00001826-0000-1000-8000-00805f9b34fb");
    private static readonly Guid IndoorBikeDataUuid = Guid.Parse("00002ad2-0000-1000-8000-00805f9b34fb");
    private static readonly Guid ControlPointUuid = Guid.Parse("00002ad9-0000-1000-8000-00805f9b34fb");

    private static readonly Guid WahooVirtualBikeServiceUuid = Guid.Parse("a026ee0d-0a7d-4ab3-97fa-f1500f9feb8b");
    private static readonly Guid WahooButtonsCharacteristicUuid = Guid.Parse("a026e03c-0a7d-4ab3-97fa-f1500f9feb8b");

    private static readonly Guid CyclingPowerServiceUuid = Guid.Parse("00001818-0000-1000-8000-00805f9b34fb");
    private static readonly Guid CyclingPowerMeasurementUuid = Guid.Parse("00002a63-0000-1000-8000-00805f9b34fb");

    private static readonly Guid HeartRateServiceUuid = Guid.Parse("0000180d-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HeartRateMeasurementUuid = Guid.Parse("00002a37-0000-1000-8000-00805f9b34fb");

    private static readonly Guid FanServiceUuid = Guid.Parse("a026ee0c-0a7d-4ab3-97fa-f1500f9feb8b");
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
    public List<WorkoutInterval> DisplayIntervals { get; private set; } = new();
    public double TotalDurationSeconds => ActiveWorkout != null 
        ? (ActiveWorkout.IsFtpPercentage ? WorkoutIntensityProfile.Count : ActiveWorkout.DurationMinutes * 60)
        : 0;

    // Active Training Plan Tracking
    public TrainingPlan? ActiveTrainingPlan { get; private set; }
    public TrainingPlanWorkout? ActivePlanWorkout { get; private set; }

    // Running State
    public bool IsWorkoutActive { get; private set; }
    public bool IsWorkoutAutoPaused { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public double IntervalSeconds { get; private set; }
    public int CurrentIntervalIndex { get; private set; }
    public double Calories { get; private set; }
    private int _zeroCadenceTicks;
    private DateTime _lastTrainerMetricsAtUtc = DateTime.MinValue;
    private const int AutoPauseDelaySeconds = 2;
    private static readonly TimeSpan TrainerMetricsFreshness = TimeSpan.FromSeconds(3);
    private const double FanMaximumTrainerSpeedKph = 40.0;
    private const int FanMaximumAppLevel = 5;
    private const double LevelPowerWatts = 140.0;
    private const double ReferencePowerWatts = 300.0;
    private const double ReferenceGradePercent = 12.0;
    private const double MinimumSimulatedGradePercent = -15.0;
    private const double MaximumSimulatedGradePercent = 20.0;

    private int _workoutIntensityPercent = 100;
    public int WorkoutIntensityPercent
    {
        get => _workoutIntensityPercent;
        set
        {
            var clampedValue = Math.Clamp(value, 50, 150);
            if (_workoutIntensityPercent == clampedValue) return;

            _workoutIntensityPercent = clampedValue;
            if (ActiveWorkout != null && !IsWorkoutActive)
            {
                SelectWorkout(ActiveWorkout);
                return;
            }

            NotifyStateChanged();
        }
    }

    // Target Power
    private string _targetMode = "ERG";
    public string TargetMode
    {
        get => _targetMode;
        set
        {
            if (_targetMode != value)
            {
                _targetMode = value;
                _isErgModeEnabled = value == "ERG";
                NotifyStateChanged();
#if WINDOWS
                _ = UpdateTrainerTargetAsync();
#endif
            }
        }
    }

    private double _targetPower = 200;
    public double TargetPower
    {
        get => _targetPower;
        set
        {
            if (_targetPower != value)
            {
                _targetPower = value;
                NotifyStateChanged();
#if WINDOWS
                _ = UpdateTrainerTargetAsync();
#endif
            }
        }
    }

    public double TargetGrade => CalculateGradeFromPower(_targetPower);

    public static double CalculateGradeFromPower(double powerWatts)
    {
        var grade = (powerWatts - LevelPowerWatts) * ReferenceGradePercent /
            (ReferencePowerWatts - LevelPowerWatts);
        return Math.Clamp(grade, MinimumSimulatedGradePercent, MaximumSimulatedGradePercent);
    }

    private bool _isPowerSlopeEnabled;
    public bool IsPowerSlopeEnabled
    {
        get => _isPowerSlopeEnabled;
        set
        {
            if (_isPowerSlopeEnabled == value) return;

            _isPowerSlopeEnabled = value;
            if (value)
            {
                _isErgModeEnabled = false;
                _targetMode = "POWER_SLOPE";
            }
            else if (_targetMode == "POWER_SLOPE")
            {
                _targetMode = "ERG";
            }

            NotifyStateChanged();
#if WINDOWS
            _ = value ? UpdateTrainerTargetAsync() : LevelTrainerAsync();
#endif
        }
    }

    private bool _isErgModeEnabled = true;
    public bool IsErgModeEnabled
    {
        get => _isErgModeEnabled;
        set
        {
            if (_isErgModeEnabled == value) return;

            _isErgModeEnabled = value;
            if (value)
            {
                _isPowerSlopeEnabled = false;
                _targetMode = "ERG";
            }

            NotifyStateChanged();
#if WINDOWS
            _ = value ? UpdateTrainerTargetAsync() : DisableErgModeAsync();
#endif
        }
    }

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

    private int _fanSpeed;
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

    private string _fanMode = "TrainerSpeed";
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
            if (value && !IsValidBluetoothAddress(SelectedFanId))
            {
                LogFan("Connection rejected: no real HEADWIND Bluetooth address is selected.");
                _fanConnected = false;
                NotifyStateChanged();
                return;
            }

            Console.WriteLine($"[Fan BLE] FanConnected setter called with value: {value} (current backing field: {_fanConnected}, SelectedFanId: '{SelectedFanId}')");
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
            if (value && !IsValidBluetoothAddress(SelectedControllableId))
            {
                _controllableConnected = false;
                ControllableConnectionStatus = "Sélectionnez un home trainer Bluetooth après la recherche";
                NotifyStateChanged();
                return;
            }

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
        
        // Load active training plan if it exists
        await LoadActiveTrainingPlanAsync();

        if (ActiveWorkout == null && AvailableWorkouts.Any())
        {
            SelectWorkout(AvailableWorkouts.First());
        }

        // Load saved devices and attempt to reconnect them
        try
        {
            var savedDevices = await context.ConnectedDevices.ToListAsync();
            foreach (var device in savedDevices)
            {
                if (string.IsNullOrEmpty(device.Address) || !IsValidBluetoothAddress(device.Address))
                {
                    if (device.IsEnabled)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"Ignoring simulated saved device '{device.Name}' ({device.Address}).");
                    }
                    continue;
                }

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
        Calories = 0;
        IsWorkoutActive = false;
        IsWorkoutAutoPaused = false;
        _zeroCadenceTicks = 0;

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

            // Scale target powers if this workout is marked as FTP percentage based
            if (workout.IsFtpPercentage)
            {
                var ftp = Profile.Ftp;
                if (ftp <= 0) ftp = 250;
                WorkoutIntensityProfile = WorkoutIntensityProfile
                    .Select(p => (int)Math.Round(p * ftp / 100.0 * WorkoutIntensityPercent / 100.0))
                    .ToList();
            }
        }
        catch
        {
            WorkoutIntensityProfile = new List<int> { 150 };
        }

        // Populate DisplayIntervals (combine consecutive identical powers)
        DisplayIntervals.Clear();
        if (WorkoutIntensityProfile.Any())
        {
            int currentPower = WorkoutIntensityProfile[0];
            int currentDuration = 1;
            for (int i = 1; i < WorkoutIntensityProfile.Count; i++)
            {
                if (WorkoutIntensityProfile[i] == currentPower)
                {
                    currentDuration++;
                }
                else
                {
                    DisplayIntervals.Add(new WorkoutInterval { TargetPower = currentPower, DurationSeconds = currentDuration });
                    currentPower = WorkoutIntensityProfile[i];
                    currentDuration = 1;
                }
            }
            DisplayIntervals.Add(new WorkoutInterval { TargetPower = currentPower, DurationSeconds = currentDuration });
        }

        if (_isErgModeEnabled)
        {
            _targetMode = "ERG";
        }
        UpdateActiveStepState();
        NotifyStateChanged();
    }

    public void StartWorkout()
    {
        if (IsWorkoutActive) return;

        if (_isErgModeEnabled)
        {
            _targetMode = "ERG";
        }
        IsWorkoutAutoPaused = false;
        _zeroCadenceTicks = 0;
        IsWorkoutActive = true;
        _simulationTimer ??= new System.Threading.Timer(Tick, null, 0, 1000);
#if WINDOWS
        _ = UpdateTrainerTargetAsync();
#endif
        NotifyStateChanged();
    }

    public void PauseWorkout()
    {
        if (!IsWorkoutActive && _simulationTimer == null) return;

        IsWorkoutActive = false;
        IsWorkoutAutoPaused = false;
        _zeroCadenceTicks = 0;
        _simulationTimer?.Dispose();
        _simulationTimer = null;
        NotifyStateChanged();
    }

    public void SkipInterval()
    {
        if (ActiveWorkout == null || WorkoutIntensityProfile.Count == 0) return;

        if (ActiveWorkout.IsFtpPercentage)
        {
            int accum = 0;
            bool found = false;
            for (int i = 0; i < DisplayIntervals.Count; i++)
            {
                accum += DisplayIntervals[i].DurationSeconds;
                if (ElapsedSeconds < accum)
                {
                    ElapsedSeconds = accum;
                    found = true;
                    break;
                }
            }
            if (!found || ElapsedSeconds >= WorkoutIntensityProfile.Count)
            {
                CompleteActiveWorkout();
                return;
            }
        }
        else
        {
            // Original logic: advance to the next 60-second block
            int nextInterval = (int)(ElapsedSeconds / 60) + 1;
            ElapsedSeconds = nextInterval * 60;
            if (ElapsedSeconds >= TotalDurationSeconds)
            {
                CompleteActiveWorkout();
                return;
            }
        }

        if (_isErgModeEnabled)
        {
            _targetMode = "ERG";
        }
        UpdateActiveStepState();
        NotifyStateChanged();
    }

    private void Tick(object? state)
    {
        if (ActiveWorkout == null) return;

        // Update simulated values first; real trainer values arrive through BLE notifications.
        SimulateMetrics();

        var hasRealTrainerMetrics = false;
#if WINDOWS
        hasRealTrainerMetrics = _ftmsCharacteristic != null;
#endif
        var metricsAreFresh = !hasRealTrainerMetrics ||
            DateTime.UtcNow - _lastTrainerMetricsAtUtc <= TrainerMetricsFreshness;
        var isPedaling = Cadence > 0 && metricsAreFresh;

        if (IsWorkoutAutoPaused)
        {
            if (!isPedaling)
            {
                NotifyStateChanged();
                return;
            }

            IsWorkoutAutoPaused = false;
            IsWorkoutActive = true;
            _zeroCadenceTicks = 0;
#if WINDOWS
            if (_isErgModeEnabled)
            {
                _ = UpdateTrainerTargetAsync();
            }
#endif
        }
        else if (IsWorkoutActive)
        {
            _zeroCadenceTicks = isPedaling ? 0 : _zeroCadenceTicks + 1;
            if (_zeroCadenceTicks >= AutoPauseDelaySeconds)
            {
                IsWorkoutActive = false;
                IsWorkoutAutoPaused = true;
                NotifyStateChanged();
                return;
            }
        }
        else
        {
            return;
        }

        ElapsedSeconds++;
        Calories += Power / 1000.0;

        // Check if workout has finished
        if (ElapsedSeconds >= TotalDurationSeconds)
        {
            CompleteActiveWorkout();
            return;
        }

        UpdateActiveStepState();
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
            double coolingEffect = 0.0;
            if (FanConnected && IsFanOn)
            {
                // Each level of fan speed cools down the rider by 0.1°C (up to 0.5°C at level 5)
                coolingEffect = FanSpeed * 0.1;
            }

            CoreTemp = Math.Round(37.2 + (Power / Profile.Ftp) * 1.2 - coolingEffect + (_random.NextDouble() * 0.1), 1);
            SkinTemp = Math.Round(32.5 - (coolingEffect * 1.5) + (_random.NextDouble() * 0.5 - 0.25), 1);
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
                UpdateFanSpeedFromTrainerMetrics();
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
    public List<DiscoveredDevice> DiscoveredDevices { get; private set; } = new();
    public bool IsScanning { get; private set; }

    private string _selectedControllableId = string.Empty;
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

    private string _selectedHrmId = string.Empty;
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

    private string _selectedMoxyId = string.Empty;
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

    private string _selectedCoreTempId = string.Empty;
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

    private string _selectedFanId = string.Empty;
    public string SelectedFanId
    {
        get => _selectedFanId;
        set
        {
            Console.WriteLine($"[Fan BLE] SelectedFanId setter called with value: '{value}' (current backing field: '{_selectedFanId}', FanConnected: {FanConnected})");
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

    private static bool IsValidBluetoothAddress(string address) =>
        ulong.TryParse(
            address,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out _);

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

        IsScanning = false;
        SelectFirstDiscoveredDeviceWhenNeeded("Controllable", SelectedControllableId,
            value => SelectedControllableId = value);
        SelectFirstDiscoveredDeviceWhenNeeded("HRM", SelectedHrmId,
            value => SelectedHrmId = value);
        SelectFirstDiscoveredDeviceWhenNeeded("Fan", SelectedFanId,
            value => SelectedFanId = value);
        NotifyStateChanged();
    }

    private void SelectFirstDiscoveredDeviceWhenNeeded(
        string deviceType,
        string selectedAddress,
        Action<string> select)
    {
        if (DiscoveredDevices.Any(device =>
            device.DeviceType == deviceType && device.Address == selectedAddress)) return;

        var first = DiscoveredDevices.FirstOrDefault(device => device.DeviceType == deviceType);
        if (first != null) select(first.Address);
    }

#if WINDOWS
    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var localName = args.Advertisement.LocalName;
        
        string deviceType = "Controllable"; // Default
        bool identified = false;

        if (!string.IsNullOrEmpty(localName))
        {
            if (localName.Contains("HEADWIND", StringComparison.OrdinalIgnoreCase) ||
                localName.Contains("Fan", StringComparison.OrdinalIgnoreCase))
            {
                deviceType = "Fan";
                identified = true;
            }
            else if (localName.Contains("KICKR", StringComparison.OrdinalIgnoreCase) || 
                localName.Contains("Tacx", StringComparison.OrdinalIgnoreCase) || 
                localName.Contains("Trainer", StringComparison.OrdinalIgnoreCase) ||
                localName.Contains("Bike", StringComparison.OrdinalIgnoreCase))
            {
                deviceType = "Controllable";
                identified = true;
            }
            else if (localName.Contains("Polar", StringComparison.OrdinalIgnoreCase) || 
                     localName.Contains("HRM", StringComparison.OrdinalIgnoreCase) || 
                     localName.Contains("H10", StringComparison.OrdinalIgnoreCase) ||
                     localName.Contains("Heart", StringComparison.OrdinalIgnoreCase))
            {
                deviceType = "HRM";
                identified = true;
            }
            else if (localName.Contains("Moxy", StringComparison.OrdinalIgnoreCase))
            {
                deviceType = "Moxy";
                identified = true;
            }
            else if (localName.Contains("Core", StringComparison.OrdinalIgnoreCase))
            {
                deviceType = "CoreTemp";
                identified = true;
            }
        }

        // If not identified by name, try to identify by Service UUIDs in the advertisement
        if (!identified)
        {
            var uuids = args.Advertisement.ServiceUuids;
            if (uuids.Contains(FtmsServiceUuid) || uuids.Contains(CyclingPowerServiceUuid))
            {
                deviceType = "Controllable";
                identified = true;
                if (string.IsNullOrEmpty(localName))
                {
                    localName = uuids.Contains(FtmsServiceUuid) ? "Wahoo KICKR Bike (FTMS)" : "Wahoo KICKR Bike (Power)";
                }
            }
            else if (uuids.Contains(HeartRateServiceUuid))
            {
                deviceType = "HRM";
                identified = true;
                if (string.IsNullOrEmpty(localName))
                {
                    localName = "Cardiofréquencemètre BLE";
                }
            }
            else if (uuids.Contains(FanServiceUuid))
            {
                deviceType = "Fan";
                identified = true;
                if (string.IsNullOrEmpty(localName))
                {
                    localName = "Wahoo Headwind (Fan)";
                }
            }
        }

        if (!identified || string.IsNullOrEmpty(localName)) return;

        var addressStr = args.BluetoothAddress.ToString("X");
        if (deviceType == "Fan")
        {
            LogFan($"HEADWIND advertisement detected: name='{localName}', address={addressStr}, RSSI={args.RawSignalStrengthInDBm}.");
        }
        System.Diagnostics.Debug.WriteLine($"[BLE Scan] Found device: '{localName}' ({addressStr}) - Type: {deviceType}");
        
        lock (DiscoveredDevices)
        {
            var existing = DiscoveredDevices.FirstOrDefault(d => d.Address == addressStr && d.DeviceType == deviceType);
            if (existing != null)
            {
                // If the existing name is generic/fallback, and we got a real name now, update it
                if ((existing.Name.Contains("FTMS") || existing.Name.Contains("Power") || existing.Name.Contains("BLE") || existing.Name.Contains("Fan")) && 
                    !string.IsNullOrEmpty(args.Advertisement.LocalName))
                {
                    existing.Name = args.Advertisement.LocalName;
                    NotifyStateChanged();
                }
                return;
            }

            DiscoveredDevices.Add(new DiscoveredDevice
            {
                Name = localName,
                Protocol = "Bluetooth",
                Address = addressStr,
                DeviceType = deviceType,
                Rssi = args.RawSignalStrengthInDBm,
                BluetoothAddressType = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ? (uint)args.BluetoothAddressType : 0
            });
            NotifyStateChanged();
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

            var discoveredDev = DiscoveredDevices.FirstOrDefault(d => d.Address == SelectedControllableId && d.DeviceType == "Controllable");
            var addressType = discoveredDev != null ? (BluetoothAddressType)discoveredDev.BluetoothAddressType : BluetoothAddressType.Public;
            
            _controllableDevice = await GetBluetoothDeviceAsync(address, addressType);
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

                        // Configure Control Point for sending targets (power, slope, resistance)
                        try
                        {
                            var cpResult = await ftmsService.GetCharacteristicsForUuidAsync(ControlPointUuid);
                            if (cpResult.Status == GattCommunicationStatus.Success && cpResult.Characteristics.Count > 0)
                            {
                                _controlPointCharacteristic = cpResult.Characteristics[0];
                                _controlPointCharacteristic.ValueChanged += OnControlPointValueChanged;
                                var cpStatus = await _controlPointCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                                    GattClientCharacteristicConfigurationDescriptorValue.Indicate);
                                
                                if (cpStatus == GattCommunicationStatus.Success)
                                {
                                    Console.WriteLine("[Trainer Control] Control Point configuré avec succès. Acquisition du contrôle...");
                                    // FTMS Request Control ne contient aucun paramètre.
                                    var buffer = new byte[] { 0x00 }.AsBuffer();
                                    await _controlPointCharacteristic.WriteValueAsync(buffer, GattWriteOption.WriteWithResponse);

                                    // Envoyer la première consigne cible
                                    _ = UpdateTrainerTargetAsync();
                                }
                                else
                                {
                                    Console.WriteLine($"[Trainer Control] Échec de l'abonnement aux indications du Control Point : {cpStatus}");
                                }
                            }
                        }
                        catch (Exception cpEx)
                        {
                            Console.WriteLine($"[Trainer Control] Erreur lors de la configuration du Control Point : {cpEx.Message}");
                        }

                        // Configurer l'écoute des boutons propriétaires Wahoo
                        try
                        {
                            var wahooService = servicesResult.Services.FirstOrDefault(s => s.Uuid == WahooVirtualBikeServiceUuid);
                            if (wahooService != null)
                            {
                                var buttonCharResult = await wahooService.GetCharacteristicsForUuidAsync(WahooButtonsCharacteristicUuid);
                                if (buttonCharResult.Status == GattCommunicationStatus.Success && buttonCharResult.Characteristics.Count > 0)
                                {
                                    _wahooButtonsCharacteristic = buttonCharResult.Characteristics[0];
                                    _wahooButtonsCharacteristic.ValueChanged += OnWahooButtonsValueChanged;
                                    var buttonStatus = await _wahooButtonsCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                                        GattClientCharacteristicConfigurationDescriptorValue.Notify);
                                    
                                    if (buttonStatus == GattCommunicationStatus.Success)
                                    {
                                        Console.WriteLine("[Wahoo Buttons] Connecté avec succès au service de boutons Wahoo !");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[Wahoo Buttons] Échec abonnement notifications boutons : {buttonStatus}");
                                    }
                                }
                            }
                        }
                        catch (Exception wahooEx)
                        {
                            Console.WriteLine($"[Wahoo Buttons] Erreur de connexion aux boutons Wahoo : {wahooEx.Message}");
                        }
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
            if (_controlPointCharacteristic != null)
            {
                _controlPointCharacteristic.ValueChanged -= OnControlPointValueChanged;
                _ = _controlPointCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
                _controlPointCharacteristic = null;
            }
            if (_wahooButtonsCharacteristic != null)
            {
                _wahooButtonsCharacteristic.ValueChanged -= OnWahooButtonsValueChanged;
                _ = _wahooButtonsCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
                _wahooButtonsCharacteristic = null;
            }
            _controllableDevice?.Dispose();
            _controllableDevice = null;
        }
        catch {}
        ControllableConnectionStatus = "Déconnecté";
    }

    private void OnControlPointValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
#if WINDOWS
        try
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            reader.ByteOrder = ByteOrder.LittleEndian;
            var data = new byte[args.CharacteristicValue.Length];
            reader.ReadBytes(data);
            string hex = BitConverter.ToString(data);
            Console.WriteLine($"[Trainer Control] Indication reçue du Control Point : {hex}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Trainer Control] Erreur lors de la lecture de la réponse du Control Point : {ex.Message}");
        }
#endif
    }

    private void OnWahooButtonsValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
#if WINDOWS
        try
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            reader.ByteOrder = ByteOrder.LittleEndian;
            var data = new byte[args.CharacteristicValue.Length];
            reader.ReadBytes(data);
            string hex = BitConverter.ToString(data);
            Console.WriteLine($"[Wahoo Buttons] État boutons reçu : {hex}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Wahoo Buttons] Erreur lors de la lecture des boutons Wahoo : {ex.Message}");
        }
#endif
    }

    private async Task UpdateTrainerTargetAsync()
    {
#if WINDOWS
        if (_controlPointCharacteristic == null) return;
        if (_targetMode == "ERG" && !_isErgModeEnabled) return;

        await _trainerWriteSemaphore.WaitAsync();
        try
        {
            byte[] payload;
            if (_targetMode == "ERG")
            {
                // Target Power (Opcode 0x05, sint16 en Watts)
                short power = (short)Math.Clamp(_targetPower, 0.0, 2000.0);
                payload = new byte[] { 0x05, (byte)(power & 0xFF), (byte)((power >> 8) & 0xFF) };
            }
            else if (_targetMode == "POWER_SLOPE")
            {
                // Set Indoor Bike Simulation Parameters (0x11): vent nul, pente en 0,01 %, coefficients nuls.
                short grade = (short)Math.Round(TargetGrade * 100.0);
                payload = new byte[]
                {
                    0x11,
                    0x00, 0x00,
                    (byte)(grade & 0xFF), (byte)((grade >> 8) & 0xFF),
                    0x00, 0x00
                };
            }
            else if (_targetMode == "SLOPE")
            {
                // Target Inclination (Opcode 0x03, sint16 en dixièmes de pourcent 0.1%)
                short inclination = (short)Math.Clamp(Math.Round(_targetPower * 10), -200, 200); // Clampe entre -20% et +20%
                payload = new byte[] { 0x03, (byte)(inclination & 0xFF), (byte)((inclination >> 8) & 0xFF) };
            }
            else if (_targetMode == "RESISTANCE")
            {
                // Target Resistance (Opcode 0x04, sint16 en unités de 0,1).
                short resistance = (short)Math.Clamp(Math.Round(_targetPower * 10), 0, 1000);
                payload = new byte[] { 0x04, (byte)(resistance & 0xFF), (byte)((resistance >> 8) & 0xFF) };
            }
            else
            {
                return;
            }

            var buffer = payload.AsBuffer();
            var status = await _controlPointCharacteristic.WriteValueAsync(buffer, GattWriteOption.WriteWithResponse);
            Console.WriteLine($"[Trainer Control] Consigne envoyée - Opcode 0x{payload[0]:X2}, Mode : {_targetMode}, Valeur : {_targetPower}, Résultat Bluetooth : {status}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Trainer Control] Erreur d'envoi de la consigne au trainer : {ex.Message}");
        }
        finally
        {
            _trainerWriteSemaphore.Release();
        }
#else
        await Task.CompletedTask;
#endif
    }

    private async Task LevelTrainerAsync()
    {
#if WINDOWS
        if (_controlPointCharacteristic == null) return;

        await _trainerWriteSemaphore.WaitAsync();
        try
        {
            // Simulation à 0 % pour remettre physiquement le KICKR BIKE à niveau.
            var payload = new byte[] { 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            await _controlPointCharacteristic.WriteValueAsync(
                payload.AsBuffer(), GattWriteOption.WriteWithResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Trainer Control] Erreur de remise à niveau : {ex.Message}");
        }
        finally
        {
            _trainerWriteSemaphore.Release();
        }
#else
        await Task.CompletedTask;
#endif
    }

    private async Task DisableErgModeAsync()
    {
#if WINDOWS
        if (_controlPointCharacteristic == null) return;

        await _trainerWriteSemaphore.WaitAsync();
        try
        {
            // Une résistance neutre libère la dernière consigne de puissance ERG.
            var payload = new byte[] { 0x04, 0x00, 0x00 };
            var status = await _controlPointCharacteristic.WriteValueAsync(
                payload.AsBuffer(), GattWriteOption.WriteWithResponse);
            Console.WriteLine($"[Trainer Control] Mode ERG désactivé - résistance neutre envoyée, résultat Bluetooth : {status}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Trainer Control] Erreur lors de la désactivation ERG : {ex.Message}");
        }
        finally
        {
            _trainerWriteSemaphore.Release();
        }
#else
        await Task.CompletedTask;
#endif
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
        _lastTrainerMetricsAtUtc = DateTime.UtcNow;

        UpdateFanSpeedFromTrainerMetrics();

        lock (PowerHistory)
        {
            PowerHistory.Add(Power);
            if (PowerHistory.Count > 60) PowerHistory.RemoveAt(0);
        }

        NotifyStateChanged();
    }

    private void UpdateFanSpeedFromTrainerMetrics()
    {
        if (!FanConnected || !IsFanOn || FanMode != "TrainerSpeed") return;

        // Cadence is checked explicitly so coasting speed never leaves the fan running.
        FanSpeed = Cadence <= 0
            ? 0
            : (int)Math.Clamp(
                Math.Round(
                    Speed / FanMaximumTrainerSpeedKph * FanMaximumAppLevel,
                    MidpointRounding.AwayFromZero),
                0,
                FanMaximumAppLevel);
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
        _lastTrainerMetricsAtUtc = DateTime.UtcNow;

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

            var discoveredDev = DiscoveredDevices.FirstOrDefault(d => d.Address == SelectedHrmId && d.DeviceType == "HRM");
            var addressType = discoveredDev != null ? (BluetoothAddressType)discoveredDev.BluetoothAddressType : BluetoothAddressType.Public;
            
            _hrmDevice = await GetBluetoothDeviceAsync(address, addressType);
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
        LogFan($"=== Connection requested; selected id='{SelectedFanId}' ===");
        Console.WriteLine($"[Fan BLE] ConnectFanAsync called for SelectedFanId: '{SelectedFanId}'");
        if (string.IsNullOrEmpty(SelectedFanId))
        {
            Console.WriteLine($"[Fan BLE] SelectedFanId is null or empty");
            return;
        }

        try
        {
            if (!ulong.TryParse(SelectedFanId, System.Globalization.NumberStyles.HexNumber, null, out ulong address))
            {
                Console.WriteLine($"[Fan BLE] SelectedFanId '{SelectedFanId}' is not a valid hex ulong address");
                return;
            }

            Console.WriteLine($"[Fan BLE] Connecting to address {address:X}...");
            var discoveredDev = DiscoveredDevices.FirstOrDefault(d => d.Address == SelectedFanId && d.DeviceType == "Fan");
            var addressType = discoveredDev != null ? (BluetoothAddressType)discoveredDev.BluetoothAddressType : BluetoothAddressType.Public;
            Console.WriteLine($"[Fan BLE] Using address type: {addressType}");

            _fanDevice = await GetBluetoothDeviceAsync(address, addressType);
            if (_fanDevice == null)
            {
                LogFan("BluetoothLEDevice creation returned null.");
                Console.WriteLine($"[Fan BLE] GetBluetoothDeviceAsync returned null device");
                return;
            }

            // Check and attempt pairing if needed
            if (!_fanDevice.DeviceInformation.Pairing.IsPaired)
            {
                Console.WriteLine("[Fan BLE] Device is not paired in Windows. Attempting programmatic pairing...");
                var pairingResult = await _fanDevice.DeviceInformation.Pairing.PairAsync();
                LogFan($"Pairing result: {pairingResult.Status}.");
                Console.WriteLine($"[Fan BLE] Programmatic pairing result status: {pairingResult.Status}");
            }
            else
            {
                Console.WriteLine("[Fan BLE] Device is already paired in Windows.");
            }

            Console.WriteLine($"[Fan BLE] Device connected. Querying services...");
            var servicesResult = await _fanDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            LogFan($"GATT service query: {servicesResult.Status}; count={servicesResult.Services.Count}.");
            Console.WriteLine($"[Fan BLE] GetGattServicesAsync status: {servicesResult.Status}");
            if (servicesResult.Status != GattCommunicationStatus.Success) return;

            var fanService = servicesResult.Services.FirstOrDefault(service => service.Uuid == FanServiceUuid);
            if (fanService == null)
            {
                LogFan($"Required service {FanServiceUuid} was not found.");
                return;
            }

            LogFan($"HEADWIND service selected: {fanService.Uuid}.");
            var charResult = await fanService.GetCharacteristicsForUuidAsync(
                FanControlUuid, BluetoothCacheMode.Uncached);
            LogFan($"Control characteristic query: {charResult.Status}; count={charResult.Characteristics.Count}.");
            var foundChar = charResult.Status == GattCommunicationStatus.Success
                ? charResult.Characteristics.FirstOrDefault()
                : null;

            if (foundChar != null)
            {
                _fanCharacteristic = foundChar;
                LogFan($"Control characteristic selected: {_fanCharacteristic.Uuid}; properties={_fanCharacteristic.CharacteristicProperties}.");
                _fanCharacteristic.ValueChanged -= OnFanValueChanged;
                _fanCharacteristic.ValueChanged += OnFanValueChanged;
                var notificationStatus = await _fanCharacteristic
                    .WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify);
                LogFan($"Notification subscription => {notificationStatus}.");
                if (notificationStatus != GattCommunicationStatus.Success)
                {
                    return;
                }

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
                LogFan("No control characteristic was found.");
                Console.WriteLine($"[Fan BLE] No writable characteristic found under any a026* service.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fan BLE] Error connecting to fan: {ex.Message}");
        }
    }

    private void DisconnectFan()
    {
        Console.WriteLine($"[Fan BLE] DisconnectFan called");
        try
        {
            if (_fanCharacteristic != null)
            {
                _fanCharacteristic.ValueChanged -= OnFanValueChanged;
                _ = _fanCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
                _fanCharacteristic = null;
            }
            _pendingFanAcknowledgement?.TrySetException(
                new InvalidOperationException("HEADWIND disconnected during a command."));
            _pendingFanAcknowledgement = null;
            _fanDevice?.Dispose();
            _fanDevice = null;
            Console.WriteLine($"[Fan BLE] Fan disconnected and resources disposed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fan BLE] Error disconnecting: {ex.Message}");
        }
    }

    private async Task WriteFanModeAsync(string mode)
    {
        Console.WriteLine($"[Fan BLE] WriteFanModeAsync called with mode: '{mode}'");
        if (_fanCharacteristic == null)
        {
            Console.WriteLine($"[Fan BLE] Cannot write mode. _fanCharacteristic is null (fan not connected)");
            return;
        }

        await _fanWriteSemaphore.WaitAsync();
        try
        {
            byte[] value = mode switch
            {
                // Trainingify calculates HR/speed coupling itself, so direct/manual
                // mode is required for every application-controlled mode.
                _ => new byte[] { 0x04, 0x04, 0x00, 0x00 }
            };

            Console.WriteLine($"[Fan BLE] Writing mode command bytes: {BitConverter.ToString(value)} to characteristic...");
            await SendFanCommandAndWaitForAcknowledgementAsync(value);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fan BLE] Error writing fan mode: {ex.Message}");
        }
        finally
        {
            _fanWriteSemaphore.Release();
        }
    }

    private void OnFanValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var data = new byte[args.CharacteristicValue.Length];
        reader.ReadBytes(data);
        if (data.Length < 4) return;

        if (data[0] == 0xFE)
        {
            LogFan($"RX ACK {BitConverter.ToString(data)}.");
            if (_pendingFanAcknowledgement != null &&
                data[1] == _pendingFanCommand && data[3] == _pendingFanValue)
            {
                _pendingFanAcknowledgement.TrySetResult(data);
            }
        }
        else if (data[0] == 0xFD)
        {
            LogFan($"RX STATE {BitConverter.ToString(data)}.");
        }
    }

    private async Task SendFanCommandAndWaitForAcknowledgementAsync(byte[] command)
    {
        if (_fanCharacteristic == null) throw new InvalidOperationException("HEADWIND is not connected.");

        _pendingFanCommand = command[0];
        _pendingFanValue = command[1];
        _pendingFanAcknowledgement = new TaskCompletionSource<byte[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            LogFan($"TX {BitConverter.ToString(command)}.");
            var status = await WriteFanValueAsync(_fanCharacteristic, command);
            if (status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"GATT write failed: {status}.");

            var acknowledgement = await _pendingFanAcknowledgement.Task
                .WaitAsync(TimeSpan.FromSeconds(1));
            if (acknowledgement[2] != 0x01)
                throw new InvalidOperationException(
                    $"HEADWIND rejected command {command[0]:X2}: status {acknowledgement[2]:X2}.");
        }
        finally
        {
            _pendingFanAcknowledgement = null;
        }
    }

    private static Task<GattCommunicationStatus> WriteFanValueAsync(
        GattCharacteristic characteristic,
        byte[] value)
    {
        var option = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;
        return characteristic.WriteValueAsync(value.AsBuffer(), option).AsTask();
    }

    private async Task WriteFanSpeedAsync(int speedLevel)
    {
        Console.WriteLine($"[Fan BLE] WriteFanSpeedAsync called with speedLevel: {speedLevel}");
        if (_fanCharacteristic == null)
        {
            Console.WriteLine($"[Fan BLE] Cannot write speed. _fanCharacteristic is null (fan not connected)");
            return;
        }

        await _fanWriteSemaphore.WaitAsync();
        try
        {
            // Direct speed control is accepted only in manual mode. The protocol
            // expects a percentage (0-100), while the UI exposes levels 0-5.
            byte[] manualMode = new byte[] { 0x04, 0x04, 0x00, 0x00 };
            await SendFanCommandAndWaitForAcknowledgementAsync(manualMode);

            byte fanPercentage = (byte)(Math.Clamp(speedLevel, 0, FanMaximumAppLevel) * 20);
            byte[] value = new byte[] { 0x02, fanPercentage, 0x00, 0x00 };

            Console.WriteLine($"[Fan BLE] Writing speed command bytes: {BitConverter.ToString(value)} to characteristic (level {speedLevel} -> {fanPercentage}%)...");
            await SendFanCommandAndWaitForAcknowledgementAsync(value);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fan BLE] Error writing fan speed: {ex.Message}");
        }
        finally
        {
            _fanWriteSemaphore.Release();
        }
    }

    private static void LogFan(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [Fan BLE] {message}";
        Console.WriteLine(line);

        try
        {
            lock (FanLogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FanLogPath)!);
                File.AppendAllText(FanLogPath, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fan BLE] Unable to write diagnostic log: {ex.Message}");
        }
    }

    private async Task<BluetoothLEDevice?> GetBluetoothDeviceAsync(ulong address, BluetoothAddressType addressType)
    {
        try
        {
            Console.WriteLine($"[BLE] Getting BluetoothLEDevice for address {address:X} with primary address type {addressType}...");
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType);
            if (device != null)
            {
                Console.WriteLine($"[BLE] Successfully connected using primary address type: {addressType}");
                return device;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLE] Error with primary address type {addressType}: {ex.Message}");
        }

        var fallbackType = addressType == BluetoothAddressType.Public ? BluetoothAddressType.Random : BluetoothAddressType.Public;
        try
        {
            Console.WriteLine($"[BLE] Retrying with fallback address type: {fallbackType}...");
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address, fallbackType);
            if (device != null)
            {
                Console.WriteLine($"[BLE] Successfully connected using fallback address type: {fallbackType}");
                return device;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLE] Error with fallback address type {fallbackType}: {ex.Message}");
        }

        try
        {
            Console.WriteLine($"[BLE] Retrying without specifying address type...");
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device != null)
            {
                Console.WriteLine($"[BLE] Successfully connected without specifying address type");
                return device;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLE] Error without address type: {ex.Message}");
        }

        Console.WriteLine($"[BLE] Failed to get BluetoothLEDevice for address {address:X}");
        return null;
    }
#endif

    private void UpdateActiveStepState()
    {
        if (ActiveWorkout == null || WorkoutIntensityProfile.Count == 0) return;

        if (ActiveWorkout.IsFtpPercentage)
        {
            var idx = (int)ElapsedSeconds;
            if (idx >= WorkoutIntensityProfile.Count) idx = WorkoutIntensityProfile.Count - 1;
            TargetPower = WorkoutIntensityProfile[idx];

            int accum = 0;
            int activeIndex = 0;
            int intervalElapsed = 0;
            for (int i = 0; i < DisplayIntervals.Count; i++)
            {
                var nextAccum = accum + DisplayIntervals[i].DurationSeconds;
                if (ElapsedSeconds < nextAccum)
                {
                    activeIndex = i;
                    intervalElapsed = (int)ElapsedSeconds - accum;
                    break;
                }
                accum = nextAccum;
            }
            CurrentIntervalIndex = activeIndex;
            IntervalSeconds = intervalElapsed;
        }
        else
        {
            int totalIntervals = WorkoutIntensityProfile.Count;
            if (totalIntervals > 0)
            {
                CurrentIntervalIndex = (int)(ElapsedSeconds / 60) % totalIntervals;
                IntervalSeconds = (int)(ElapsedSeconds % 60);
                TargetPower = WorkoutIntensityProfile[CurrentIntervalIndex];
            }
        }
    }

    public async Task<List<WorkoutTemplateDto>> LoadWorkoutTemplatesAsync()
    {
        try
        {
            using var stream = await Microsoft.Maui.Storage.FileSystem.OpenAppPackageFileAsync("wwwroot/workouts.json");
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            return System.Text.Json.JsonSerializer.Deserialize<List<WorkoutTemplateDto>>(json, options) ?? new();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading workout templates: {ex.Message}");
            return new();
        }
    }

    public void SelectWorkoutTemplate(WorkoutTemplateDto template)
    {
        var durationSeconds = CalculateDurationSeconds(template.Segments);
        SelectWorkout(new Workout
        {
            Id = 0,
            Name = template.Name,
            Type = "Plan",
            DurationMinutes = (int)Math.Ceiling(durationSeconds / 60.0),
            IntensityProfileJson = ConvertSegmentsToIntensityProfileJson(template.Segments),
            IsFtpPercentage = true
        });
    }

    public async Task<List<string>> GetAvailableTemplateGroupsAsync()
    {
        var templates = await LoadWorkoutTemplatesAsync();
        return templates
            .Select(t => t.Path.Split(new[] { '\\', '/' })[0])
            .Distinct()
            .OrderBy(g => g)
            .ToList();
    }

    public async Task LoadActiveTrainingPlanAsync()
    {
        using var context = await _dbFactory.CreateDbContextAsync();
        ActiveTrainingPlan = await context.TrainingPlans
            .Include(p => p.Workouts)
            .FirstOrDefaultAsync(p => p.IsActive);

        if (ActiveTrainingPlan != null)
        {
            var workouts = ActiveTrainingPlan.Workouts.OrderBy(w => w.SequenceOrder).ToList();
            if (ActiveTrainingPlan.CurrentWorkoutIndex < workouts.Count)
            {
                ActivePlanWorkout = workouts[ActiveTrainingPlan.CurrentWorkoutIndex];

                var mappedWorkout = new Workout
                {
                    Id = -ActivePlanWorkout.Id,
                    Name = ActivePlanWorkout.Name,
                    Type = "Plan",
                    DurationMinutes = ActivePlanWorkout.DurationMinutes,
                    IntensityProfileJson = ActivePlanWorkout.IntensityProfileJson,
                    IsFtpPercentage = true
                };

                SelectWorkout(mappedWorkout);
            }
            else
            {
                ActivePlanWorkout = null;
            }
        }
        else
        {
            ActivePlanWorkout = null;
        }
        NotifyStateChanged();
    }

    public async Task CancelActiveTrainingPlanAsync()
    {
        using var context = await _dbFactory.CreateDbContextAsync();
        var activePlans = await context.TrainingPlans.Where(p => p.IsActive).ToListAsync();
        foreach (var p in activePlans)
        {
            p.IsActive = false;
        }
        await context.SaveChangesAsync();
        await LoadActiveTrainingPlanAsync();
    }

    public async Task UpdateWorkoutDurationAsync(int workoutId, int newDurationMinutes)
    {
        if (newDurationMinutes <= 0) return;

        using var context = await _dbFactory.CreateDbContextAsync();
        if (workoutId < 0)
        {
            // Plan Workout
            var planWorkoutId = -workoutId;
            var planWorkout = await context.TrainingPlanWorkouts.FirstOrDefaultAsync(w => w.Id == planWorkoutId);
            if (planWorkout != null && planWorkout.DurationMinutes != newDurationMinutes)
            {
                var cleanJson = planWorkout.IntensityProfileJson.Trim('[', ']');
                if (!string.IsNullOrWhiteSpace(cleanJson))
                {
                    var oldProfile = cleanJson.Split(',').Select(s => int.Parse(s.Trim())).ToList();
                    if (oldProfile.Count > 0)
                    {
                        int newSize = newDurationMinutes * 60; // plan workouts always represent 1 second per element
                        int oldSize = oldProfile.Count;

                        var newProfile = new List<int>();
                        for (int i = 0; i < newSize; i++)
                        {
                            double ratio = (double)i / newSize;
                            int oldIdx = (int)Math.Min(oldSize - 1, (int)Math.Floor(ratio * oldSize));
                            newProfile.Add(oldProfile[oldIdx]);
                        }
                        planWorkout.IntensityProfileJson = "[" + string.Join(",", newProfile) + "]";
                    }
                }

                planWorkout.DurationMinutes = newDurationMinutes;
                await context.SaveChangesAsync();

                // Reload the active training plan to update cache and active workout
                await LoadActiveTrainingPlanAsync();
            }
        }
        else
        {
            // Static Workout
            var workout = await context.Workouts.FirstOrDefaultAsync(w => w.Id == workoutId);
            if (workout != null && workout.DurationMinutes != newDurationMinutes)
            {
                var cleanJson = workout.IntensityProfileJson.Trim('[', ']');
                if (!string.IsNullOrWhiteSpace(cleanJson))
                {
                    var oldProfile = cleanJson.Split(',').Select(s => int.Parse(s.Trim())).ToList();
                    if (oldProfile.Count > 0)
                    {
                        int newSize = workout.IsFtpPercentage ? newDurationMinutes * 60 : newDurationMinutes;
                        int oldSize = oldProfile.Count;

                        var newProfile = new List<int>();
                        for (int i = 0; i < newSize; i++)
                        {
                            double ratio = (double)i / newSize;
                            int oldIdx = (int)Math.Min(oldSize - 1, (int)Math.Floor(ratio * oldSize));
                            newProfile.Add(oldProfile[oldIdx]);
                        }
                        workout.IntensityProfileJson = "[" + string.Join(",", newProfile) + "]";
                    }
                }

                workout.DurationMinutes = newDurationMinutes;
                await context.SaveChangesAsync();

                // Refresh cache in service
                AvailableWorkouts = await context.Workouts.ToListAsync();
                if (ActiveWorkout != null && ActiveWorkout.Id == workoutId)
                {
                    var updatedWorkout = AvailableWorkouts.First(w => w.Id == workoutId);
                    SelectWorkout(updatedWorkout);
                }
                NotifyStateChanged();
            }
        }
    }

    public async Task CompleteWorkoutManuallyAsync(Workout workout)
    {
        using var context = await _dbFactory.CreateDbContextAsync();
        var session = new CompletedSession
        {
            WorkoutName = workout.Name,
            Date = DateTime.Now,
            AveragePower = Math.Round(Profile.Ftp * 0.75),
            MaxPower = Profile.Ftp * 1.1,
            AverageHeartRate = 135,
            DurationSeconds = workout.DurationMinutes * 60
        };
        context.CompletedSessions.Add(session);
        await context.SaveChangesAsync();

        if (workout.Id < 0)
        {
            // Plan Workout: advance active training plan
            await AdvanceActiveTrainingPlanAsync();
        }
        else
        {
            NotifyStateChanged();
        }
    }

    public async Task StartNewTrainingPlanAsync(string groupName, List<WorkoutTemplateDto> templates)
    {
        using var context = await _dbFactory.CreateDbContextAsync();

        // Deactivate existing plans
        var activePlans = await context.TrainingPlans.Where(p => p.IsActive).ToListAsync();
        foreach (var p in activePlans)
        {
            p.IsActive = false;
        }

        var newPlan = new TrainingPlan
        {
            Name = groupName,
            GroupName = groupName,
            CurrentWorkoutIndex = 0,
            IsActive = true
        };

        context.TrainingPlans.Add(newPlan);
        await context.SaveChangesAsync();

        int order = 0;
        foreach (var t in templates)
        {
            var intensityJson = ConvertSegmentsToIntensityProfileJson(t.Segments);
            var durationSecs = CalculateDurationSeconds(t.Segments);

            var planWorkout = new TrainingPlanWorkout
            {
                TrainingPlanId = newPlan.Id,
                Name = t.Name,
                Description = t.Description ?? "",
                DurationMinutes = (int)Math.Ceiling(durationSecs / 60.0),
                IntensityProfileJson = intensityJson,
                IsCompleted = false,
                SequenceOrder = order++,
                Path = t.Path
            };
            context.TrainingPlanWorkouts.Add(planWorkout);
        }

        await context.SaveChangesAsync();
        await LoadActiveTrainingPlanAsync();
    }

    public async void CompleteActiveWorkout()
    {
        PauseWorkout();

        using (var context = await _dbFactory.CreateDbContextAsync())
        {
            var session = new CompletedSession
            {
                WorkoutName = ActiveWorkout?.Name ?? "Entraînement",
                Date = DateTime.Now,
                AveragePower = PowerHistory.Any() ? Math.Round(PowerHistory.Average()) : 0,
                MaxPower = PowerHistory.Any() ? PowerHistory.Max() : 0,
                AverageHeartRate = HeartRateHistory.Any() ? Math.Round(HeartRateHistory.Average()) : 0,
                DurationSeconds = ElapsedSeconds
            };
            context.CompletedSessions.Add(session);
            await context.SaveChangesAsync();
        }

        await AdvanceActiveTrainingPlanAsync();
        NotifyStateChanged();
    }

    public async Task AdvanceActiveTrainingPlanAsync()
    {
        if (ActiveTrainingPlan == null || ActivePlanWorkout == null) return;

        using var context = await _dbFactory.CreateDbContextAsync();
        var plan = await context.TrainingPlans
            .Include(p => p.Workouts)
            .FirstOrDefaultAsync(p => p.Id == ActiveTrainingPlan.Id);

        if (plan != null)
        {
            var workouts = plan.Workouts.OrderBy(w => w.SequenceOrder).ToList();
            var currentWk = workouts.FirstOrDefault(w => w.Id == ActivePlanWorkout.Id);
            if (currentWk != null)
            {
                currentWk.IsCompleted = true;
            }

            plan.CurrentWorkoutIndex++;
            if (plan.CurrentWorkoutIndex >= workouts.Count)
            {
                plan.IsActive = false; // completed plan
            }

            await context.SaveChangesAsync();
        }

        await LoadActiveTrainingPlanAsync();
    }

    private static double CalculateDurationSeconds(List<WorkoutSegmentDto> segments)
    {
        double totalSeconds = 0;
        foreach (var s in segments)
        {
            if (s.T == "i")
            {
                totalSeconds += (s.R ?? 1) * ((s.D1 ?? 0) + (s.D2 ?? 0));
            }
            else
            {
                totalSeconds += s.D1 ?? 0;
            }
        }
        return totalSeconds;
    }

    private static string ConvertSegmentsToIntensityProfileJson(List<WorkoutSegmentDto> segments)
    {
        var profile = new List<int>();
        foreach (var s in segments)
        {
            if (s.T == "s")
            {
                int p1 = (int)(s.P1 ?? 50);
                int d1 = s.D1 ?? 0;
                for (int i = 0; i < d1; i++) profile.Add(p1);
            }
            else if (s.T == "r")
            {
                double p1 = s.P1 ?? 50;
                double p2 = s.P2 ?? 50;
                int d1 = s.D1 ?? 0;
                for (int i = 0; i < d1; i++)
                {
                    double t = d1 > 1 ? (double)i / (d1 - 1) : 0;
                    int p = (int)Math.Round(p1 + (p2 - p1) * t);
                    profile.Add(p);
                }
            }
            else if (s.T == "i")
            {
                int p1 = (int)(s.P1 ?? 50);
                int d1 = s.D1 ?? 0;
                int p2 = (int)(s.P2 ?? 50);
                int d2 = s.D2 ?? 0;
                int r = s.R ?? 1;
                for (int loop = 0; loop < r; loop++)
                {
                    for (int i = 0; i < d1; i++) profile.Add(p1);
                    for (int i = 0; i < d2; i++) profile.Add(p2);
                }
            }
            else if (s.T == "f")
            {
                int d1 = s.D1 ?? 0;
                for (int i = 0; i < d1; i++) profile.Add(50);
            }
        }

        return "[" + string.Join(",", profile) + "]";
    }

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
    public uint BluetoothAddressType { get; set; } = 0; // 0 = Public, 1 = Random
}

public class WorkoutInterval
{
    public int TargetPower { get; set; }
    public int DurationSeconds { get; set; }
}

public class WorkoutTemplateDto
{
    public string Path { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string Author { get; set; } = null!;
    public List<WorkoutSegmentDto> Segments { get; set; } = new();
}

public class WorkoutSegmentDto
{
    public string T { get; set; } = null!; // "s" (steady), "r" (ramp), "i" (interval), "f" (free ride)
    public double? P1 { get; set; }        // target power 1 (percentage of FTP)
    public double? P2 { get; set; }        // target power 2 (percentage of FTP)
    public int? D1 { get; set; }           // duration 1 (seconds)
    public int? D2 { get; set; }           // duration 2 (seconds)
    public int? R { get; set; }            // repetitions
}
