namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsResourcePlanner
{
    /// <summary>Declares the production orders the resource targets owe, scoped to the economy/resource Need.</summary>
    public Declared DeclareOrders(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        InternalDeclareOrders(snapshot, projection, benches, ct).For(EconomyFamily.Resource.FilingKey.Value);

    private Declared InternalDeclareOrders(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        var floors = new List<ResourceFloor>();
        foreach (var (resource, target) in reviewer.ResourceTargets(snapshot))
        {
            if (resource.Value == "Beer")
            {
                var wort = projection.Facts.Items.Wort;
                if (wort.Value == "")
                {
                    return WorkRules.Abstaining(Cause.UnreadWort);
                }

                floors.Add(new ResourceFloor { Resource = wort, Target = target, Reserve = true });
                continue;
            }

            floors.Add(new ResourceFloor { Resource = resource, Target = target });
        }

        return ResourceRules.DeclareResourceOrders(new ResourceOrderRequest { Floors = [.. floors], Stock = ResourceStockRules.Count(projection.Snapshot.Resources), Benches = [.. benches], Crafts = projection.Facts.Crafts });
    }
}
