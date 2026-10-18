namespace RimGovernor.Brain.BuildingRuntime;

public interface IActivityTaskSource
{
    Task<O.ActivityTaskRead> ReadActivityTasksAsync(O.ActivityTaskRequest request, CancellationToken ct);
    Task<O.ConstructionActivityRead> ReadConstructionActivityAsync(O.ConstructionActivityRequest request, CancellationToken ct);
}
