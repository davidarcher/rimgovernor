namespace RimGovernor.Brain.Observation;

/// <summary>Serves the load's definition catalog. A colony source without it reads no definitions; one that is also a <see cref="IResearchSource"/> makes a definition available by its finished research.</summary>
public interface IDefinitionSource
{
    Task<DefinitionCatalog?> DefinitionCatalogAsync(C.Identity id, CancellationToken cancellationToken);
}

/// <summary>The research read whose finished projects make a definition available.</summary>
public interface IResearchSource
{
    Task<(ResearchRead Read, Result Receipt)> ReadResearchAsync(C.Identity id, CancellationToken cancellationToken);
}

/// <summary>
/// What resolves a definition name: the catalog, the finished research projects (unknown without a research
/// read) and the colony's edible crop rows.
/// </summary>
internal sealed class DefinitionFacts
{
    public required DefinitionCatalog Catalog { get; init; }

    public Fact<IReadOnlySet<string>> Finished { get; init; }

    public required IReadOnlyDictionary<string, O.EdibleCrop> Crops { get; init; }

    /// <summary>Appends to <paramref name="held"/> the resolved row of every name it does not already hold.</summary>
    public List<PlanningDefinition> AppendDefinitions(List<PlanningDefinition> held, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (held.Exists(d => d.Name == name))
            {
                continue;
            }
            held.Add(Resolve(name));
        }
        return held;
    }

    /// <summary>
    /// Name's planning row: the view over the catalog's def rows, available when every research prerequisite
    /// is finished, with the map's crop facts. A name the catalog does not list as buildable or sowable is
    /// unavailable; a row the view needs and the catalog lacks is an error.
    /// </summary>
    public PlanningDefinition Resolve(string name)
    {
        PlanningDefinition? view;
        try
        {
            view = PlanningView(Catalog, name);
        }
        catch (DomainException e)
        {
            throw new DomainException($"planning definition {name}: {e.Message}");
        }
        if (view is null)
        {
            return new PlanningDefinition { Name = name, Available = Known(false) };
        }
        var d = view;
        if (Finished.TryGetValue(out var finished))
        {
            var available = true;
            foreach (var prerequisite in d.Research)
            {
                available = available && finished.Contains(prerequisite);
            }
            d = d with { Available = Known(available) };
        }
        else if (d.Research.Count == 0)
        {
            d = d with { Available = Known(true) };
        }
        if (Crops.TryGetValue(name, out var crop))
        {
            d = d with
            {
                NutritionDemandPerDay = Optional(crop.HasNutritionDemandPerDay, crop.NutritionDemandPerDay),
                DietAllowed = Optional(crop.HasDietAllowed, crop.DietAllowed),
            };
        }
        return d;
    }
}

public static partial class ObservationPackage
{
    /// <summary>The planning definitions every planning read resolves from the definition catalog; a read names any other it needs.</summary>
    public static readonly IReadOnlyList<string> StarterDefinitions =
    [
        "Barricade", "Battery", "Bed", "ButcherSpot", "Campfire", "ChemfuelPoweredGenerator", "Cooler", "Door",
        "Fence", "FenceGate", "FueledStove", "GeothermalGenerator", "Heater", "PartySpot", "PassiveCooler", "PenMarker",
        "Plant_Corn", "Plant_Potato", "Plant_Rice", "PowerConduit", "Sandbags", "SimpleResearchBench", "SleepingSpot",
        "SolarGenerator", "StandingLamp", "TableStonecutter", "Wall", "WindTurbine", "WoodFiredGenerator",
        PolicyFunctions.ClimateDefs().VentDef, // the vent the tiler prices (#2915)
    ];

    /// <summary>
    /// Reads the catalog and finished research through <paramref name="source"/>; null when it serves no
    /// definitions (no catalog). A research read that fails leaves the finished projects unknown.
    /// </summary>
    internal static async Task<DefinitionFacts?> ReadDefinitionFacts(object source, C.Identity id, O.PlanningFacts? planning, CancellationToken cancellationToken)
    {
        if (source is not IDefinitionSource native)
        {
            return null;
        }
        var catalog = await native.DefinitionCatalogAsync(id, cancellationToken).ConfigureAwait(false);
        if (catalog is null)
        {
            return null;
        }
        var finished = default(Fact<IReadOnlySet<string>>);
        if (source is IResearchSource research)
        {
            try
            {
                var (read, _) = await research.ReadResearchAsync(id, cancellationToken).ConfigureAwait(false);
                finished = FinishedSet(read.Finished);
            }
            catch (BridgeUnavailableException)
            {
                // The research read is unavailable: the finished projects stay unknown.
            }
        }
        return new DefinitionFacts { Catalog = catalog, Finished = finished, Crops = CropRows(planning) };
    }

    private static Fact<IReadOnlySet<string>> FinishedSet(IEnumerable<string> names) => Known<IReadOnlySet<string>>(new HashSet<string>(names, StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, O.EdibleCrop> CropRows(O.PlanningFacts? planning)
    {
        var rows = new Dictionary<string, O.EdibleCrop>(StringComparer.Ordinal);
        if (planning is not null)
        {
            foreach (var row in planning.Crops)
            {
                rows[row.DefName] = row;
            }
        }
        return rows;
    }
}
