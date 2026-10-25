namespace RimGovernor.Brain.Runtime;

public interface IActivityTaskSource
{
    Task<RimGovernor.Brain.GameFacts.Observations.ActivityTaskRead> ReadActivityTasksAsync(RimGovernor.Brain.GameOrders.Observations.ActivityTaskRequest request, CancellationToken ct);
    Task<RimGovernor.Brain.GameFacts.Observations.ConstructionActivityRead> ReadConstructionActivityAsync(RimGovernor.Brain.GameOrders.Observations.ConstructionActivityRequest request, CancellationToken ct);
}
