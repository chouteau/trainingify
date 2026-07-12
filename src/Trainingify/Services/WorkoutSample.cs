namespace Trainingify.Services;

public sealed record WorkoutSample(
    DateTime TimestampUtc,
    double ElapsedSeconds,
    double Power,
    double Cadence,
    double SpeedKph,
    double HeartRate,
    double DistanceKilometers,
    double Calories,
    double? CoreTemperature,
    double? SkinTemperature,
    double SmO2,
    double TotalHemoglobin);
