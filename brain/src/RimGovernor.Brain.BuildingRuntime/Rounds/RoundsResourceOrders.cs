namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsResourcePlanner
{
    /// <summary>Declares the production orders the resource targets owe, scoped to MaintainResource.</summary>
    public Declared DeclareOrders(CancellationToken ct, GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches) =>
        InternalDeclareOrders(ct, snapshot, projection, benches).For(ConcernId.MaintainResource);

    private Declared InternalDeclareOrders(CancellationToken ct, GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches)
    {
        var floors = new List<ResourceFloor>();
        foreach (var (resource, target) in reviewer.ResourceTargets(snapshot))
        {
            if (resource.Value == "Beer")
            {
                var wort = projection.Facts.Items.Wort;
                if (wort.Value == "")
                {
                    return PolicyFunctions.Abstaining(Cause.UnreadWort);
                }

                floors.Add(new ResourceFloor { Resource = wort, Target = target, Reserve = true });
                continue;
            }

            floors.Add(new ResourceFloor { Resource = resource, Target = target });
        }

        return PolicyFunctions.DeclareResourceOrders(new ResourceOrderRequest { Floors = floors, Stock = projection.Resources, Benches = Known(benches) });
    }
}
