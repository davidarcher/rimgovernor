namespace RimGovernor.Brain.BuildingRuntime;

public interface IFoodActivitySource
{
    Task<O.FoodActivityRead> ReadFoodActivityAsync(O.FoodActivityRequest request, CancellationToken ct);
}

public interface IActivityTaskSource
{
    Task<O.ActivityTaskRead> ReadActivityTasksAsync(O.ActivityTaskRequest request, CancellationToken ct);
    Task<O.ConstructionActivityRead> ReadConstructionActivityAsync(O.ConstructionActivityRequest request, CancellationToken ct);
}
