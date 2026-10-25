namespace RimGovernor.Brain.Policy;

// Fields (#2961, #3005). The survey's whole-map field patches (Zone) no longer make the plan's field zones: the
// station chain's field nodes do, sized by FieldCapacityTarget, placed by the embedder beside the rooms and
// written through FieldZone. The tiled plan carries them at every review.
public static partial class LayoutFieldReplanRules
{
    /// <summary>
    /// The judgment table of the crop a colony's field is planned for while nothing is sown: a new colony sows
    /// nothing, and a field node exists only for a crop, so without a default no field is ever planned and
    /// nothing is ever sown. The first of the table the catalog offers as an edible, available crop wins (rice,
    /// the fast staple that tolerates poor soil, then the others a tribal start can plant); the field planner's
    /// own choice (PlanField) replaces it the moment a crop is sown.
    /// </summary>
    public static readonly IReadOnlyList<string> FieldCrops = ["Plant_Rice", "Plant_Potato", "Plant_Corn"];

    /// <summary>The crop a field is planned for while none is sown: the first of the FieldCrops table that choices offers as available and edible.</summary>
    public static (CropChoice? Crop, bool Ok) DefaultFieldCrop(IReadOnlyList<CropChoice> choices)
    {
        foreach (var name in FieldCrops)
        {
            foreach (var c in choices)
            {
                var avail = c.Available;
                var edible = c.Edible;
                if (c.Name == name && avail && edible)
                {
                    return (c, true);
                }
            }
        }
        return (null, false);
    }

    /// <summary>The zones less the survey's field patches, then fields.</summary>
    internal static List<LayoutZone> WithFieldZones(IReadOnlyList<LayoutZone> zones, IReadOnlyList<LayoutZone>? fields)
    {
        var output = new List<LayoutZone>(zones.Count + (fields?.Count ?? 0));
        foreach (var z in zones)
        {
            if (z.Kind != LayoutZoneKind.Field)
            {
                output.Add(z);
            }
        }
        if (fields is not null)
        {
            output.AddRange(fields);
        }
        return output;
    }
}
