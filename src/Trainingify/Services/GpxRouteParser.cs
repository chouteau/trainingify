using System.Globalization;
using System.Xml.Linq;

namespace Trainingify.Services;

public interface IGpxRouteParser
{
    Task<SuperClimbRoute> ParseAsync(Stream stream, CancellationToken cancellationToken = default);
}

public sealed class GpxRouteParser : IGpxRouteParser
{
    private const double EarthRadiusMeters = 6_371_000;

    public async Task<SuperClimbRoute> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        var raw = document.Descendants().Where(e => e.Name.LocalName == "trkpt").Select(e =>
        {
            var lat = Parse(e.Attribute("lat")?.Value);
            var lon = Parse(e.Attribute("lon")?.Value);
            var elevation = Parse(e.Elements().FirstOrDefault(x => x.Name.LocalName == "ele")?.Value);
            return (lat, lon, elevation);
        }).Where(p => p.lat is >= -90 and <= 90 && p.lon is >= -180 and <= 180).ToList();

        if (raw.Count < 2) throw new InvalidDataException("Le GPX doit contenir au moins deux points de trace valides.");

        var cleaned = new List<(double lat, double lon, double elevation, double distance)>();
        foreach (var point in raw)
        {
            if (cleaned.Count == 0) { cleaned.Add((point.lat, point.lon, point.elevation, 0)); continue; }
            var previous = cleaned[^1];
            var delta = Distance(previous.lat, previous.lon, point.lat, point.lon);
            if (delta < 1 || delta > 2_000) continue;
            cleaned.Add((point.lat, point.lon, point.elevation, previous.distance + delta));
        }
        if (cleaned.Count < 2) throw new InvalidDataException("La trace GPX ne contient pas assez de points distincts.");

        var result = new List<SuperClimbPoint>(cleaned.Count);
        for (var i = 0; i < cleaned.Count; i++)
        {
            var from = i;
            while (from > 0 && cleaned[i].distance - cleaned[from].distance < 75) from--;
            var to = i;
            while (to < cleaned.Count - 1 && cleaned[to].distance - cleaned[i].distance < 75) to++;
            var run = cleaned[to].distance - cleaned[from].distance;
            var grade = run < 20 ? 0 : (cleaned[to].elevation - cleaned[from].elevation) / run * 100;
            result.Add(new(cleaned[i].lat, cleaned[i].lon, cleaned[i].elevation, cleaned[i].distance,
                Math.Clamp(grade, -15, 20)));
        }
        return new SuperClimbRoute(result);
    }

    private static double Parse(string? value) => double.TryParse(value, NumberStyles.Float,
        CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;

    private static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1); var dLon = ToRadians(lon2 - lon1);
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Pow(Math.Sin(dLon / 2), 2);
        return EarthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
    private static double ToRadians(double value) => value * Math.PI / 180;
}
