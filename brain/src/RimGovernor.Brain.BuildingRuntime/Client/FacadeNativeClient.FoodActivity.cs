using RimGovernor.Brain.BuildingRuntime;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class FacadeNativeClient : IActivityTaskSource
{
    Task<O.ActivityTaskRead> IActivityTaskSource.ReadActivityTasksAsync(O.ActivityTaskRequest request, CancellationToken ct) =>
        Facade.ReadActivityTasksAsync(request, ct);
    Task<O.ConstructionActivityRead> IActivityTaskSource.ReadConstructionActivityAsync(O.ConstructionActivityRequest request, CancellationToken ct) =>
        Facade.ReadConstructionActivityAsync(request, ct);
}
