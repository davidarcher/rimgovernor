namespace RimGovernor.Brain.BuildingRuntime;

public interface IFoodActivitySource
{
    Task<O.FoodActivityRead> ReadFoodActivityAsync(O.FoodActivityRequest request, CancellationToken ct);
}
