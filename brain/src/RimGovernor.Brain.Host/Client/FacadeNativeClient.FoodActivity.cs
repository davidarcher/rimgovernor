using RimGovernor.Brain.BuildingRuntime;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Host;

internal sealed partial class FacadeNativeClient : IActivityTaskSource
{
    Task<O.RecipeActivityRead> IActivityTaskSource.ReadRecipeActivityAsync(O.RecipeActivityRequest request, CancellationToken ct) =>
        Facade.ReadRecipeActivityAsync(request, ct);
    Task<O.ActivityTaskRead> IActivityTaskSource.ReadActivityTasksAsync(O.ActivityTaskRequest request, CancellationToken ct) =>
        Facade.ReadActivityTasksAsync(request, ct);
    Task<O.ConstructionActivityRead> IActivityTaskSource.ReadConstructionActivityAsync(O.ConstructionActivityRequest request, CancellationToken ct) =>
        Facade.ReadConstructionActivityAsync(request, ct);
}
