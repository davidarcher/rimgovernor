namespace RimGovernor.Brain.Observation;

/// <summary>
/// One row of a native policy database: the policy's load id, label, the player pawns currently holding it and
/// whether it is the database default. Allowed is a reading policy's allowed book definitions or a food
/// policy's allowed foods; Drugs a drug policy's entries that allow anything.
/// </summary>
public sealed record PolicyEntry
{
    public string ID { get; init; } = "";

    public string Label { get; init; } = "";

    public IReadOnlyList<PawnId> Pawns { get; init; } = [];

    public bool Default { get; init; }

    public IReadOnlyList<string> Allowed { get; init; } = [];

    public IReadOnlyList<DrugPolicyEntry> Drugs { get; init; } = [];
}

/// <summary>One Area_Allowed on the colony map and the pawns restricted to it there.</summary>
public sealed record AllowedArea
{
    public string ID { get; init; } = "";

    public string Label { get; init; } = "";

    public IReadOnlyList<PawnId> Pawns { get; init; } = [];
}

/// <summary>Every outfit, drug, food and reading policy and every allowed area the colony holds.</summary>
public sealed record Policies
{
    public IReadOnlyList<PolicyEntry> Outfit { get; init; } = [];

    public IReadOnlyList<PolicyEntry> Drug { get; init; } = [];

    public IReadOnlyList<PolicyEntry> Food { get; init; } = [];

    public IReadOnlyList<PolicyEntry> Reading { get; init; } = [];

    public IReadOnlyList<AllowedArea> AllowedAreas { get; init; } = [];

    /// <summary>Every book definition and its kind, read from the definition catalog by DecodeColony; empty without one.</summary>
    public IReadOnlyList<Book> Books { get; init; } = [];

    /// <summary>The colony map biome's disease hediffs (BiomeDef.diseases), set from the catalog.</summary>
    public IReadOnlyList<string> BiomeDiseases { get; init; } = [];

    /// <summary>Every food definition and its kind, read from the definition catalog by DecodeColony; empty without one.</summary>
    public IReadOnlyList<Food> Foods { get; init; } = [];

    /// <summary>The prisoners and tame animals holding food policies.</summary>
    public IReadOnlyList<FoodEater> FoodEaters { get; init; } = [];
}

public static partial class ObservationPackage
{
    private static List<PawnId> PawnIds(IEnumerable<string> ids)
    {
        var result = new List<PawnId>();
        foreach (var id in ids)
        {
            result.Add(new PawnId(id));
        }
        return result;
    }

    public static Fact<Policies> ColonyPolicies(O.PolicySection? section)
    {
        var f = section?.Observed;
        if (f is null)
        {
            return default;
        }
        List<PolicyEntry> Entries(IEnumerable<O.PolicyEntry> rows)
        {
            var result = new List<PolicyEntry>();
            foreach (var row in rows)
            {
                var drugs = new List<DrugPolicyEntry>();
                foreach (var d in row.DrugEntries)
                {
                    drugs.Add(new DrugPolicyEntry(d.DrugDef, d.AllowedForJoy, d.AllowedForAddiction, d.AllowScheduled, d.DaysFrequency, d.OnlyIfMoodBelow, d.OnlyIfJoyBelow, (int)d.TakeToInventory));
                }
                result.Add(new PolicyEntry { ID = row.Id, Label = row.Label, Pawns = PawnIds(row.PawnIds), Default = row.Default, Allowed = [.. row.AllowedDefs], Drugs = drugs });
            }
            return result;
        }
        var eaters = new List<FoodEater>();
        foreach (var e in f.FoodEaters)
        {
            var traits = new List<PawnTrait>();
            foreach (var t in e.PawnTraits)
            {
                traits.Add(new PawnTrait { Name = t.DefName, Degree = (int)t.Degree });
            }
            eaters.Add(new FoodEater { Pawn = new PawnId(e.PawnId), Animal = e.Kind == O.FoodEaterKind.Animal, Precepts = [.. e.Precepts], Traits = traits });
        }
        var areas = new List<AllowedArea>();
        foreach (var row in f.AllowedAreas)
        {
            areas.Add(new AllowedArea { ID = row.Id, Label = row.Label, Pawns = PawnIds(row.PawnIds) });
        }
        return Known(new Policies { Outfit = Entries(f.Outfit), Drug = Entries(f.Drug), Food = Entries(f.Food), Reading = Entries(f.Reading), FoodEaters = eaters, AllowedAreas = areas });
    }

    /// <summary>
    /// Gives each animal food eater the foods its race can ever eat, from the catalog's race row. An animal
    /// whose pawn row or race the frame does not hold is dropped: no diet is written for it. A prisoner's
    /// traits are resolved against the catalog.
    /// </summary>
    internal static IReadOnlyList<FoodEater> AnimalFoodEaters(IReadOnlyList<FoodEater> eaters, Pawns pawns, AnimalRaceCatalog races, DefinitionCatalog? catalog)
    {
        var output = new List<FoodEater>(eaters.Count);
        foreach (var eater in eaters)
        {
            var e = eater;
            var traits = new List<PawnTrait>(e.Traits.Count);
            foreach (var trait in e.Traits)
            {
                traits.Add(ResolveTrait(catalog, trait));
            }
            e = e with { Traits = traits };
            if (e.Animal)
            {
                if (!pawns.TryGetValue(e.Pawn.Value, out var row) || row is null)
                {
                    continue;
                }
                if (!races.Race(new Resource(row.Pawn?.DefName ?? ""), out var race))
                {
                    continue;
                }
                e = e with { Edible = race.Edible };
            }
            output.Add(e);
        }
        return output;
    }

    /// <summary>The Safe allowed area's load id, "" when the map has none.</summary>
    internal static Fact<string> ShelterArea(Fact<Policies> p) => AllowedAreaID(p, SafeAreaLabel);

    /// <summary>The load id of the allowed area labelled <paramref name="label"/>, "" when the map has none.</summary>
    internal static Fact<string> AllowedAreaID(Fact<Policies> p, string label)
    {
        if (!p.TryGetValue(out var v))
        {
            return Unknown<string>();
        }
        foreach (var a in v.AllowedAreas)
        {
            if (a.Label == label)
            {
                return Known(a.ID);
            }
        }
        return Known("");
    }
}
