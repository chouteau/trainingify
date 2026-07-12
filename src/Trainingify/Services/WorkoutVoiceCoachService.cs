using Microsoft.Maui.Media;

namespace Trainingify.Services;

public sealed class WorkoutVoiceCoachService : IDisposable
{
    private const int CadenceToleranceRpm = 5;
    private const int CadenceSampleCount = 8;
    private static readonly TimeSpan CadenceReminderDelay = TimeSpan.FromSeconds(25);

    private readonly TrainingStateService _state;
    private readonly Queue<double> _cadenceSamples = new();
    private readonly SemaphoreSlim _speechLock = new(1, 1);
    private CancellationTokenSource? _speechCancellation;
    private int _lastIntervalIndex = -1;
    private int _announcedPreviewInterval = -1;
    private bool _wasActive;
    private DateTime _lastCadenceReminderUtc = DateTime.MinValue;

    public WorkoutVoiceCoachService(TrainingStateService state)
    {
        _state = state;
        _state.OnStateChanged += ObserveWorkout;
    }

    public bool IsEnabled { get; set; } = true;
    public string LastMessage { get; private set; } = "Coach vocal prêt";

    public async Task TestVoiceAsync() => await SpeakAsync("Coach vocal activé. Bonne séance !", true);

    private void ObserveWorkout()
    {
        if (_state.ActiveWorkout == null)
        {
            Reset();
            return;
        }

        if (!_state.IsWorkoutActive)
        {
            _wasActive = false;
            return;
        }

        if (!_wasActive)
        {
            _wasActive = true;
            if (_state.ElapsedSeconds <= 1)
            {
                _ = SpeakAsync($"Début de la séance {_state.ActiveWorkout.Name}. {DescribeInterval(_state.CurrentIntervalIndex, true)}", true);
            }
        }

        if (_state.CurrentIntervalIndex != _lastIntervalIndex)
        {
            _lastIntervalIndex = _state.CurrentIntervalIndex;
            _announcedPreviewInterval = -1;
            _cadenceSamples.Clear();
            if (_state.ElapsedSeconds > 1)
            {
                _ = SpeakAsync($"Nouveau palier. {DescribeInterval(_state.CurrentIntervalIndex, true)}", true);
            }
        }

        AnnounceNextInterval();
        CheckCadence();
    }

    private void AnnounceNextInterval()
    {
        var current = GetInterval(_state.CurrentIntervalIndex);
        var nextIndex = _state.CurrentIntervalIndex + 1;
        if (current == null || nextIndex >= _state.DisplayIntervals.Count) return;

        var remaining = current.DurationSeconds - (int)_state.IntervalSeconds;
        if (remaining is > 15 or < 14 || _announcedPreviewInterval == nextIndex) return;

        _announcedPreviewInterval = nextIndex;
        _ = SpeakAsync($"Dans 15 secondes, {DescribeInterval(nextIndex, false)}");
    }

    private void CheckCadence()
    {
        if (_state.Cadence <= 0 || _state.IntervalSeconds < 10) return;

        _cadenceSamples.Enqueue(_state.Cadence);
        while (_cadenceSamples.Count > CadenceSampleCount) _cadenceSamples.Dequeue();
        if (_cadenceSamples.Count < CadenceSampleCount || DateTime.UtcNow - _lastCadenceReminderUtc < CadenceReminderDelay) return;

        var target = GetInterval(_state.CurrentIntervalIndex)?.TargetCadence ?? 0;
        var average = _cadenceSamples.Average();
        string? message = average < target - CadenceToleranceRpm
            ? $"Pédale un peu plus vite. Cible {target} tours par minute."
            : average > target + CadenceToleranceRpm
                ? $"Ralentis légèrement. Cible {target} tours par minute."
                : null;

        if (message == null) return;
        _lastCadenceReminderUtc = DateTime.UtcNow;
        _ = SpeakAsync(message);
    }

    private string DescribeInterval(int index, bool includePosition)
    {
        var interval = GetInterval(index);
        if (interval == null) return "";

        var intensity = _state.Profile.Ftp > 0
            ? (int)Math.Round(interval.TargetPower / _state.Profile.Ftp * 100)
            : 0;
        var position = includePosition ? $"Palier {index + 1} sur {_state.DisplayIntervals.Count}. " : "";
        return $"{position}Durée {FormatDuration(interval.DurationSeconds)}, intensité {intensity} pour cent, {interval.TargetPower} watts, cadence {interval.TargetCadence} tours par minute.";
    }

    private WorkoutInterval? GetInterval(int index) =>
        index >= 0 && index < _state.DisplayIntervals.Count ? _state.DisplayIntervals[index] : null;

    private static string FormatDuration(int seconds)
    {
        var minutes = seconds / 60;
        var remainder = seconds % 60;
        if (minutes == 0) return $"{remainder} secondes";
        if (remainder == 0) return $"{minutes} minute{(minutes > 1 ? "s" : "")}";
        return $"{minutes} minute{(minutes > 1 ? "s" : "")} et {remainder} secondes";
    }

    private async Task SpeakAsync(string message, bool interrupt = false)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(message)) return;
        LastMessage = message;

        if (interrupt) _speechCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _speechCancellation = cancellation;
        var lockTaken = false;
        try
        {
            await _speechLock.WaitAsync(cancellation.Token);
            lockTaken = true;
            await TextToSpeech.Default.SpeakAsync(message, new SpeechOptions { Pitch = 1.0f, Volume = 1.0f }, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VoiceCoach] {ex.Message}");
        }
        finally
        {
            if (lockTaken) _speechLock.Release();
            cancellation.Dispose();
        }
    }

    private void Reset()
    {
        _lastIntervalIndex = -1;
        _announcedPreviewInterval = -1;
        _wasActive = false;
        _cadenceSamples.Clear();
    }

    public void Dispose()
    {
        _state.OnStateChanged -= ObserveWorkout;
        _speechCancellation?.Cancel();
        _speechCancellation?.Dispose();
        _speechLock.Dispose();
    }
}
