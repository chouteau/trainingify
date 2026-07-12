using Dynastream.Fit;
using FitDateTime = Dynastream.Fit.DateTime;

namespace Trainingify.Services;

public static class FitActivityFileService
{
    public static byte[] Create(IReadOnlyList<WorkoutSample> samples, double ftp, double normalizedPower)
    {
        if (samples.Count == 0)
        {
            return [];
        }

        var start = new FitDateTime(samples[0].TimestampUtc);
        var end = new FitDateTime(samples[^1].TimestampUtc);
        var duration = (float)samples[^1].ElapsedSeconds;
        var distanceMeters = (float)(samples[^1].DistanceKilometers * 1000d);
        var averagePower = (ushort)Math.Clamp(Math.Round(samples.Average(s => s.Power)), 0, ushort.MaxValue);
        var maxPower = (ushort)Math.Clamp(Math.Round(samples.Max(s => s.Power)), 0, ushort.MaxValue);
        var averageCadence = (byte)Math.Clamp(Math.Round(samples.Average(s => s.Cadence)), 0, byte.MaxValue);
        var maxCadence = (byte)Math.Clamp(Math.Round(samples.Max(s => s.Cadence)), 0, byte.MaxValue);
        var averageHeartRate = (byte)Math.Clamp(Math.Round(samples.Average(s => s.HeartRate)), 0, byte.MaxValue);
        var maxHeartRate = (byte)Math.Clamp(Math.Round(samples.Max(s => s.HeartRate)), 0, byte.MaxValue);
        var averageSpeed = (float)(samples.Average(s => s.SpeedKph) / 3.6d);
        var maxSpeed = (float)(samples.Max(s => s.SpeedKph) / 3.6d);
        var calories = (ushort)Math.Clamp(Math.Round(samples[^1].Calories), 0, ushort.MaxValue);

        using var stream = new MemoryStream();
        var encoder = new Encode(ProtocolVersion.V20);
        encoder.Open(stream);

        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Activity);
        fileId.SetManufacturer(Manufacturer.Development);
        fileId.SetProduct(1);
        fileId.SetSerialNumber(1);
        fileId.SetTimeCreated(start);
        encoder.Write(fileId);

        var timerStart = new EventMesg();
        timerStart.SetTimestamp(start);
        timerStart.SetEvent(Event.Timer);
        timerStart.SetEventType(EventType.Start);
        encoder.Write(timerStart);

        foreach (var sample in samples)
        {
            var record = new RecordMesg();
            record.SetTimestamp(new FitDateTime(sample.TimestampUtc));
            record.SetPower((ushort)Math.Clamp(Math.Round(sample.Power), 0, ushort.MaxValue));
            record.SetCadence((byte)Math.Clamp(Math.Round(sample.Cadence), 0, byte.MaxValue));
            record.SetHeartRate((byte)Math.Clamp(Math.Round(sample.HeartRate), 0, byte.MaxValue));
            record.SetEnhancedSpeed((float)(sample.SpeedKph / 3.6d));
            record.SetDistance((float)(sample.DistanceKilometers * 1000d));
            record.SetCalories((ushort)Math.Clamp(Math.Round(sample.Calories), 0, ushort.MaxValue));
            if (sample.CoreTemperature is { } temperature)
            {
                record.SetCoreTemperature((float)temperature);
            }
            encoder.Write(record);
        }

        var timerStop = new EventMesg();
        timerStop.SetTimestamp(end);
        timerStop.SetEvent(Event.Timer);
        timerStop.SetEventType(EventType.StopAll);
        encoder.Write(timerStop);

        var lap = new LapMesg();
        PopulateSummary(lap, start, end, duration, distanceMeters, averagePower, maxPower,
            averageCadence, maxCadence, averageHeartRate, maxHeartRate, averageSpeed, maxSpeed,
            calories, normalizedPower);
        lap.SetMessageIndex(0);
        lap.SetLapTrigger(LapTrigger.SessionEnd);
        encoder.Write(lap);

        var session = new SessionMesg();
        PopulateSummary(session, start, end, duration, distanceMeters, averagePower, maxPower,
            averageCadence, maxCadence, averageHeartRate, maxHeartRate, averageSpeed, maxSpeed,
            calories, normalizedPower);
        session.SetMessageIndex(0);
        session.SetFirstLapIndex(0);
        session.SetNumLaps(1);
        session.SetSport(Sport.Cycling);
        session.SetSubSport(SubSport.IndoorCycling);
        session.SetThresholdPower((ushort)Math.Clamp(Math.Round(ftp), 0, ushort.MaxValue));
        session.SetIntensityFactor((float)(ftp > 0 ? normalizedPower / ftp * 1000d : 0));
        session.SetTrainingStressScore((float)(ftp > 0 ? duration * normalizedPower * (normalizedPower / ftp) / (ftp * 3600d) * 100d : 0));
        encoder.Write(session);

        var activity = new ActivityMesg();
        activity.SetTimestamp(end);
        activity.SetTotalTimerTime(duration);
        activity.SetNumSessions(1);
        activity.SetType(Activity.Manual);
        activity.SetEvent(Event.Activity);
        activity.SetEventType(EventType.Stop);
        encoder.Write(activity);

        encoder.Close();
        return stream.ToArray();
    }

    private static void PopulateSummary(dynamic message, FitDateTime start, FitDateTime end,
        float duration, float distance, ushort avgPower, ushort maxPower, byte avgCadence,
        byte maxCadence, byte avgHeartRate, byte maxHeartRate, float avgSpeed, float maxSpeed,
        ushort calories, double normalizedPower)
    {
        message.SetTimestamp(end);
        message.SetStartTime(start);
        message.SetTotalElapsedTime(duration);
        message.SetTotalTimerTime(duration);
        message.SetTotalDistance(distance);
        message.SetTotalCalories(calories);
        message.SetAvgPower(avgPower);
        message.SetMaxPower(maxPower);
        message.SetAvgCadence(avgCadence);
        message.SetMaxCadence(maxCadence);
        message.SetAvgHeartRate(avgHeartRate);
        message.SetMaxHeartRate(maxHeartRate);
        message.SetEnhancedAvgSpeed(avgSpeed);
        message.SetEnhancedMaxSpeed(maxSpeed);
        message.SetNormalizedPower((ushort)Math.Clamp(Math.Round(normalizedPower), 0, ushort.MaxValue));
        message.SetEvent(Event.Lap);
        message.SetEventType(EventType.Stop);
        message.SetSport(Sport.Cycling);
        message.SetSubSport(SubSport.IndoorCycling);
    }
}
