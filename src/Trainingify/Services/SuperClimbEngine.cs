using Microsoft.EntityFrameworkCore;
using Trainingify.Data;

namespace Trainingify.Services;

public interface ISuperClimbEngine
{
    event Action? Changed;
    SuperClimbCatalogItem? Selected { get; }
    SuperClimbRoute? Route { get; }
    SuperClimbPosition? Position { get; }
    bool IsActive { get; }
    bool IsPaused { get; }
    double ProgressPercent { get; }
    Task SelectAsync(SuperClimbCatalogItem climb);
    Task StartAsync(bool resume = false);
    Task PauseAsync();
    Task TickAsync();
    Task StopAsync(bool save);
    void ClearSelection();
}

public sealed class SuperClimbEngine(IGpxRouteParser parser, ITrainerGradeController trainer,
    TrainingStateService state, IDbContextFactory<TrainingifyDbContext> dbFactory) : ISuperClimbEngine
{
    private DateTime _lastGradeSent = DateTime.MinValue;
    private double _lastGrade = double.NaN;
    private double _resumeMeters;
    private double _sessionMeters;
    private double _elapsedSeconds;
    private DateTime _lastTickUtc;
    public event Action? Changed;
    public SuperClimbCatalogItem? Selected { get; private set; }
    public SuperClimbRoute? Route { get; private set; }
    public SuperClimbPosition? Position { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsPaused { get; private set; }
    public double ProgressPercent => Route is null ? 0 : Math.Clamp((Position?.DistanceMeters ?? 0) / Route.DistanceMeters * 100, 0, 100);

    public async Task SelectAsync(SuperClimbCatalogItem climb)
    {
        await StopAsync(false);
        await using var stream = await FileSystem.OpenAppPackageFileAsync(climb.GpxAsset);
        Route = await parser.ParseAsync(stream);
        Selected = climb;
        Position = At(0);
        using var db = await dbFactory.CreateDbContextAsync();
        var saved = await db.SuperClimbSessions.Where(x => x.SuperClimbId == climb.Id && x.Status == "Saved")
            .OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefaultAsync();
        _resumeMeters = saved?.DistanceMeters ?? 0;
        Changed?.Invoke();
    }

    public async Task StartAsync(bool resume = false)
    {
        if (Route is null || Selected is null || !trainer.IsSupported || !state.ControllableConnected) return;
        if (resume && Position is not null) _resumeMeters = Position.DistanceMeters;
        else if (!resume) _resumeMeters = 0;
        _sessionMeters = 0;
        _lastTickUtc = DateTime.UtcNow;
        IsActive = true; IsPaused = false;
        await trainer.StartSimulationAsync();
        Changed?.Invoke();
    }

    public Task PauseAsync() { IsPaused = true; IsActive = false; Changed?.Invoke(); return Task.CompletedTask; }

    public async Task TickAsync()
    {
        if (!IsActive || IsPaused || Route is null) return;
        var now = DateTime.UtcNow;
        var seconds = Math.Clamp((now - _lastTickUtc).TotalSeconds, 0, 2);
        _lastTickUtc = now;
        _elapsedSeconds += seconds;
        _sessionMeters += Math.Max(0, state.Speed) / 3.6 * seconds;
        var distance = _resumeMeters + _sessionMeters;
        Position = At(distance);
        if (Position is not null && (DateTime.UtcNow - _lastGradeSent >= TimeSpan.FromSeconds(1) || Math.Abs(Position.GradePercent - _lastGrade) >= .3))
        {
            await trainer.SetGradeAsync(Position.GradePercent); _lastGrade = Position.GradePercent; _lastGradeSent = DateTime.UtcNow;
        }
        if (distance >= Route.DistanceMeters) await CompleteAsync();
        Changed?.Invoke();
    }

    public async Task StopAsync(bool save)
    {
        if (Selected is not null && Position is not null && (IsActive || IsPaused))
        {
            using var db = await dbFactory.CreateDbContextAsync();
            db.SuperClimbSessions.Add(new SuperClimbSession { SuperClimbId = Selected.Id, DistanceMeters = Position.DistanceMeters,
                DurationSeconds = _elapsedSeconds, Status = save ? "Saved" : "Abandoned", UpdatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        IsActive = false; IsPaused = false; await trainer.StopSimulationAsync(); Changed?.Invoke();
    }

    public void ClearSelection() { Selected = null; Route = null; Position = null; Changed?.Invoke(); }

    private async Task CompleteAsync()
    {
        IsActive = false;
        using var db = await dbFactory.CreateDbContextAsync();
        db.SuperClimbSessions.Add(new SuperClimbSession { SuperClimbId = Selected!.Id, DistanceMeters = Route!.DistanceMeters,
            DurationSeconds = _elapsedSeconds, Status = "Completed", UpdatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync(); await trainer.StopSimulationAsync();
    }

    private SuperClimbPosition? At(double distance)
    {
        if (Route is null || Route.Points.Count == 0) return null;
        distance = Math.Clamp(distance, 0, Route.DistanceMeters);
        var index = 1; while (index < Route.Points.Count && Route.Points[index].DistanceMeters < distance) index++;
        if (index >= Route.Points.Count) { var p = Route.Points[^1]; return new(p.Latitude, p.Longitude, p.ElevationMeters, p.DistanceMeters, p.GradePercent); }
        var a = Route.Points[index - 1]; var b = Route.Points[index]; var span = Math.Max(1, b.DistanceMeters - a.DistanceMeters); var t = (distance - a.DistanceMeters) / span;
        return new(a.Latitude + (b.Latitude - a.Latitude) * t, a.Longitude + (b.Longitude - a.Longitude) * t,
            a.ElevationMeters + (b.ElevationMeters - a.ElevationMeters) * t, distance, a.GradePercent + (b.GradePercent - a.GradePercent) * t);
    }
}
