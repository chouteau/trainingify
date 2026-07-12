namespace Trainingify.Services;

public interface ITrainerGradeController
{
    bool IsSupported { get; }
    Task StartSimulationAsync();
    Task SetGradeAsync(double gradePercent);
    Task StopSimulationAsync();
}

public sealed class TrainerGradeController(TrainingStateService state) : ITrainerGradeController
{
#if WINDOWS
    public bool IsSupported => true;
#else
    public bool IsSupported => false;
#endif
    public Task StartSimulationAsync() => state.SetRouteGradeAsync(0);
    public Task SetGradeAsync(double gradePercent) => state.SetRouteGradeAsync(Math.Clamp(gradePercent, -15, 20));
    public Task StopSimulationAsync() => state.SetRouteGradeAsync(0);
}
