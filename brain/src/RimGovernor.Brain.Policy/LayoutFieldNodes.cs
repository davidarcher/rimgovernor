namespace RimGovernor.Brain.Policy;

// Fields as outdoor nodes of the station graph (#2885). A food crop's field is land use the colony owes by
// demand: the node asks the cells FieldCapacityTarget gives (the count at which FieldCoverage reads 1, no
// stored margin) and sits in the same graph as the rooms, because the crops-to-kitchen and crops-to-storage
// haul is the largest flow and a field's position trades against a room's in one walk term. Pasture, wood and
// mining are not nodes: nothing at runtime reads pasture or wood and ore is fixed by the map, so they stay the
// zoning masks (Zone). The node names its mask, ZoneField (soil of at least FertilityMin); placement and the
// tiler's mask are the next stage, and crop choice is the caller's: Crops are the crops already chosen.
public static partial class PolicyConstants
{
    /// <summary>Why the graph holds a field: the crop's capacity target.</summary>
    public const string ReasonFieldDemand = "field_crop_capacity_target";

    /// <summary>A crop's haul from its field.</summary>
    public const string ReasonFlowCrop = "flow_crop";
}

/// <summary>The outdoor land one crop needs.</summary>
public sealed record FieldNode
{
    public string Id { get; init; } = string.Empty;

    /// <summary>The crop's name.</summary>
    public string Crop { get; init; } = string.Empty;

    /// <summary>The resource the crop's harvest yields.</summary>
    public Resource Harvests { get; init; }

    /// <summary>The field's cells: FieldCapacityTarget for the colony.</summary>
    public int Area { get; init; }

    /// <summary>The zoning mask the field sits on (ZoneField).</summary>
    public ZoneKind Mask { get; init; }

    /// <summary>The least soil fertility the crop grows in; the cells where both hold are the candidates the tiler gets.</summary>
    public double FertilityMin { get; init; }

    public string Reason { get; init; } = string.Empty;
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
        var colonists = Unknown<long>();
        if (In.Population.Colonists > 0)
        {
            colonists = Known((long)In.Population.Colonists);
        }
        var output = new List<FieldNode>();
        foreach (var crop in In.Crops)
        {
            if (!crop.Edible.TryGetValue(out var edible) || !edible)
            {
                continue;
            }
            if (!PolicyFunctions.FieldCapacityTarget(colonists, crop, In.FoodReserveDays).TryGetValue(out var cells))
            {
                continue;
            }
            var hk = crop.Harvests.TryGetValue(out var harvests);
            crop.FertilityMin.TryGetValue(out var fertility);
            var node = new FieldNode
            {
                Id = PolicyFunctions.FieldId(crop.Name),
                Crop = crop.Name,
                Harvests = harvests,
                Area = cells,
                Mask = ZoneKind.Field,
                FertilityMin = Math.Max(fertility, PolicyFunctions.ZoneFieldFertility),
                Reason = PolicyConstants.ReasonFieldDemand,
            };
            output.Add(node);
            var uk = crop.UnitsPerCell.TryGetValue(out var perCell);
            var gk = crop.GrowDays.TryGetValue(out var days);
            if (!hk || !uk || !gk || !PolicyFunctions.FieldPositive(perCell) || !PolicyFunctions.FieldPositive(days))
            {
                continue;
            }
            var (store, ok) = StoreFor(harvests);
            if (ok)
            {
                AddEdge(node.Id, store, cells * perCell / days, false, PolicyConstants.ReasonFlowCrop);
            }
            foreach (var l in loads)
            {
                foreach (var pick in PolicyFunctions.RecipePicks(In.Recipes[l.Recipe]))
                {
                    if (pick.Resource == harvests)
                    {
                        AddEdge(node.Id, PolicyFunctions.NodeId(l.Station, StationPurpose.Work), l.Runs * pick.Count, false, PolicyConstants.ReasonFlowCrop);
                    }
                }
            }
        }
        output.Sort((a, c) => string.CompareOrdinal(a.Id, c.Id));
        return output;
    }
}

public static partial class PolicyFunctions
{
    internal static string FieldId(string crop) => "field/" + crop;
}
