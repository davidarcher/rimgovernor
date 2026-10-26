using Identity = RimGovernor.Brain.GameAccess.ObservationIdentity;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;
/// <summary>Serves the load's definition catalog. A colony source without it reads no definitions; one that is also a <see cref = "IResearchSource"/> makes a definition available by its finished research.</summary>
public interface IDefinitionSource
{
    /// <summary>The load's catalog; a catalog that cannot be read throws.</summary>
    Task<DefinitionCatalog> DefinitionCatalogAsync(RimGovernor.Brain.GameFacts.Common.Identity id, CancellationToken cancellationToken);
}

/// <summary>The research read whose finished projects make a definition available.</summary>
/// <summary>
/// What resolves a definition name: the catalog, the required finished research projects and the colony's edible crop rows.
/// </summary>
internal sealed class DefinitionFacts
{
    public required DefinitionCatalog Catalog { get; init; }
    public required IReadOnlySet<string> Finished { get; init; }
    public required IReadOnlyDictionary<string, RimGovernor.Brain.GameFacts.Observations.EdibleCrop> Crops { get; init; }

    /// <summary>Appends to <paramref name = "held"/> the resolved row of every name it does not already hold.</summary>
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
            view = PlanningViewReads.PlanningView(Catalog, name);
        }
        catch (DomainException e)
        {
            throw new DomainException($"planning definition {name}: {e.Message}");
        }

        if (view is null)
        {
            return new PlanningDefinition
            {
                Name = name,
                Availability = PlanningAvailability.NotInCatalog
            };
        }

        var d = view;
        d = d with
        {
            Available = d.Research.All(Finished.Contains)
        };
        if (Crops.TryGetValue(name, out var crop))
        {
            d = d with
            {
                Diet = (d.Diet ?? new PlanningDiet())with
                {
                    NutritionDemandPerDay = crop.NutritionDemandPerDay,
                    DietAllowed = crop.DietAllowed
                }
            };
        }

        if (d.Harvest is { Edible: true } && d.Diet is null)
        {
            throw new RequiredFactException("edible crop diet:" + name);
        }

        return d;
    }
}

public static class DefinitionsReads
{
    /// <summary>The planning definitions every planning read resolves from the definition catalog; a read names any other it needs.</summary>
    public static readonly IReadOnlyList<string> StarterDefinitions = [nameof(RimWorld.ThingDefOf.Barricade), nameof(RimWorld.ThingDefOf.Battery), nameof(RimWorld.ThingDefOf.Bed), "ButcherSpot", nameof(RimWorld.ThingDefOf.Campfire), nameof(RimWorld.ThingDefOf.Cooler), nameof(RimWorld.ThingDefOf.Door), "Fence", "FenceGate", "FueledStove", nameof(RimWorld.ThingDefOf.GeothermalGenerator), nameof(RimWorld.ThingDefOf.Heater), nameof(RimWorld.ThingDefOf.PartySpot), nameof(RimWorld.ThingDefOf.PassiveCooler), "PenMarker", "Plant_Corn", nameof(RimWorld.ThingDefOf.Plant_Potato), "Plant_Rice", nameof(RimWorld.ThingDefOf.PowerConduit), nameof(RimWorld.ThingDefOf.Sandbags), nameof(RimWorld.ThingDefOf.SimpleResearchBench), nameof(RimWorld.ThingDefOf.SleepingSpot), nameof(RimWorld.ThingDefOf.StandingLamp), "TableStonecutter", nameof(RimWorld.ThingDefOf.Wall), KitchenSeparationRules.ButcherTableDefinition, ClimateRules.ClimateDefs().VentDef, // the vent the tiler prices (#2915)
 ];
    /// <summary>
    /// Reads the catalog and finished research through <paramref name = "source"/>; null when it serves no
    /// definitions. A failed research read raises; an unread project set cannot authorize availability.
    /// </summary>
    internal static IReadOnlySet<string> FinishedSet(IEnumerable<string> names) => new HashSet<string>(names, StringComparer.Ordinal);
    internal static Dictionary<string, RimGovernor.Brain.GameFacts.Observations.EdibleCrop> CropRows(IEnumerable<RimGovernor.Brain.GameFacts.Observations.EdibleCrop> crops) => crops.ToDictionary(crop => crop.DefName, StringComparer.Ordinal);
}
