namespace RimGovernor.Brain.Policy;

// Fields as outdoor nodes of the station graph (#2885). A food crop's field is land use the colony owes by
// demand: the node asks the cells FieldCapacityTarget gives (the count at which FieldCoverage reads 1, no
// stored margin) and sits in the same graph as the rooms, because the crops-to-kitchen and crops-to-storage
// haul is the largest flow and a field's position trades against a room's in one walk term. Pasture, wood and
// mining are not nodes: nothing at runtime reads pasture or wood and ore is fixed by the map, so they stay the
// zoning masks (Zone). The node names its mask, ZoneField (soil of at least FertilityMin); placement and the
// tiler's mask are the next stage, and crop choice is the caller's: Crops are the crops already chosen.
/// <summary>The outdoor land one crop needs.</summary>
public sealed record FieldNode
{
    public string Id { get; init; } = string.Empty;

    /// <summary>The field's cells: FieldCapacityTarget for the colony.</summary>
    public int Area { get; init; }

    /// <summary>The least soil fertility the crop grows in; the cells where both hold are the candidates the tiler gets.</summary>
    public double FertilityMin { get; init; }

}

public sealed partial class StationGraph
{
    /// <summary>The field with the id.</summary>
    public (FieldNode? Field, bool Ok) FieldNode(string id)
    {
        foreach (var f in Fields)
        {
            if (f.Id == id)
            {
                return (f, true);
            }
        }
        return (null, false);
    }
}

internal sealed partial class GraphBuilder
{
    /// <summary>
    /// Adds a node for each edible crop with a known capacity target and the crop's haul edges: its harvest to
    /// the store that holds it, and to each station whose recipe takes it in. A crop whose target is unknown
    /// gets no node; an unknown yield or grow time adds the node without flow edges.
    /// </summary>
    public List<FieldNode> FieldNodes(IReadOnlyList<RecipeLoad> loads)
    {
        var output = new List<FieldNode>();
        // A colony with no colonist sizes no field.
        long colonists = In.Population.Colonists;
        if (colonists <= 0)
        {
            return output;
        }
        foreach (var crop in In.Crops)
        {
            var edible = crop.Edible;
            if (!edible)
            {
                continue;
            }
            var cells = FieldCapacityRules.FieldCapacityTarget(colonists, crop, In.FoodReserveDays);
            var harvests = crop.Harvests;
            var fertility = crop.FertilityMin;
            var node = new FieldNode
            {
                Id = LayoutFieldNodesRules.FieldId(crop.Name),
                Area = cells,
                FertilityMin = Math.Max(fertility, LayoutZoningRules.ZoneFieldFertility),
            };
            output.Add(node);
            var perCell = crop.UnitsPerCell;
            var days = crop.GrowDays;
            if (!FieldCapacityRules.FieldPositive(perCell) || !FieldCapacityRules.FieldPositive(days))
            {
                continue;
            }
            var (store, ok) = StoreFor(harvests);
            if (ok)
            {
                AddEdge(node.Id, store, cells * perCell / days, false);
            }
            foreach (var l in loads)
            {
                foreach (var pick in LayoutGraphRules.RecipePicks(In.Recipes[l.Recipe]))
                {
                    if (pick.Resource == harvests)
                    {
                        AddEdge(node.Id, LayoutGraphRules.NodeId(l.Station, StationPurpose.Work), l.Runs * pick.Count, false);
                    }
                }
            }
        }
        output.Sort((a, c) => string.CompareOrdinal(a.Id, c.Id));
        return output;
    }
}

public static partial class LayoutFieldNodesRules
{
    internal static string FieldId(string crop) => "field/" + crop;
}
