using RimGovernor.Brain.BuildingRuntime;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Host;

internal sealed partial class FacadeNativeClient
{
    Task<O.FoodActivityRead> IFoodActivitySource.ReadFoodActivityAsync(O.FoodActivityRequest request, CancellationToken ct) =>
        Facade.ReadFoodActivityAsync(request, ct);
}
