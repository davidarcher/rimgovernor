namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsResourcePlanner
{
    /// <summary>Declares the production orders the resource targets owe, scoped to MaintainResource.</summary>
    public Declared DeclareOrders(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        InternalDeclareOrders(snapshot, projection, benches, ct).For(PolicyFunctions.MaintainResource);

    private Declared InternalDeclareOrders(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
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

        return PolicyFunctions.DeclareResourceOrders(new ResourceOrderRequest { Floors = [.. floors], Stock = projection.Resources, Benches = Known<EquatableArray<GearBench>>([.. benches]) });
    }
}
