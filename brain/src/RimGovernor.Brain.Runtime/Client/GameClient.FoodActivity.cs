using RimGovernor.Brain.Runtime;

namespace RimGovernor.Brain.Runtime;

public sealed partial class GameClient : IActivityTaskSource
{
    Task<RimGovernor.Brain.GameFacts.Observations.ActivityTaskRead> IActivityTaskSource.ReadActivityTasksAsync(RimGovernor.Brain.GameOrders.Observations.ActivityTaskRequest request, CancellationToken ct) =>
        Facade.ReadActivityTasksAsync(request, ct);
    Task<RimGovernor.Brain.GameFacts.Observations.ConstructionActivityRead> IActivityTaskSource.ReadConstructionActivityAsync(RimGovernor.Brain.GameOrders.Observations.ConstructionActivityRequest request, CancellationToken ct) =>
        Facade.ReadConstructionActivityAsync(request, ct);
}
