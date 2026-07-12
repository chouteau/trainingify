namespace Trainingify.Services;

public sealed record SuperClimbPoint(double Latitude, double Longitude, double ElevationMeters,
    double DistanceMeters, double GradePercent);

public sealed record SuperClimbRoute(IReadOnlyList<SuperClimbPoint> Points)
{
    public double DistanceMeters => Points.Count == 0 ? 0 : Points[^1].DistanceMeters;
    public double ElevationGainMeters => Points.Zip(Points.Skip(1), (a, b) => Math.Max(0, b.ElevationMeters - a.ElevationMeters)).Sum();
    public double AverageGradePercent => DistanceMeters <= 0 || Points.Count < 2 ? 0 :
        (Points[^1].ElevationMeters - Points[0].ElevationMeters) / DistanceMeters * 100;
}

public sealed record SuperClimbPosition(double Latitude, double Longitude, double ElevationMeters,
    double DistanceMeters, double GradePercent);

public sealed record SuperClimbCatalogItem(int Id, string Name, string Region, string Country,
    string Description, string ImageUrl, string SourceUrl, string GpxAsset, string Difficulty);

public enum SuperClimbSessionStatus { Saved, Active, Completed, Abandoned }
