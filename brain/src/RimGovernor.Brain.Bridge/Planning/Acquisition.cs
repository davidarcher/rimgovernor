using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using CatalogStats = RimGovernor.Brain.Bridge.CatalogStats;
using Cell = RimGovernor.Brain.Domain.Cell;
namespace RimGovernor.Brain.Bridge;

public static class AcquisitionReads
{
    /// <summary>
    /// Decodes the native-approved harvest/hunt source census of one colony facts read; a source the tables or the
    /// catalog cannot head fails the read. Hunt rows <c>HuntGate</c> holds are left out (<c>ReviewFacts.HuntHolds</c>).
    /// </summary>
    public static EquatableArray<AcquisitionSource> ColonyAcquisition(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v, Tables tables, DefinitionCatalog? catalog) =>
        DecodeAcquisition(v, tables, catalog).Sources;

    /// <summary>
    /// Maps the census rows. A native read carries the raw hunt census, and every hunt row passes the hunt gate:
    /// a held row is not a source but a hold.
    /// </summary>
    internal static (EquatableArray<AcquisitionSource> Sources, IReadOnlyList<HuntHold> Holds) DecodeAcquisition(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v, Tables tables, DefinitionCatalog? definitions)
    {
        var catalog = ColonyPlanningReads.RequireCatalog(definitions);
        var races = catalog.AnimalRaces();
        var census = HuntCensusOf(v.HuntCensus, catalog);
        var rows = new List<AcquisitionSource>();
        var holds = new List<HuntHold>();
        foreach (var row in v.Acquisition)
        {
            var source = tables.Entity(row.Source) ?? throw new BridgeContractException($"acquisition source {row.Source.Id} has no entity head in the tables");
            var raceKnown = races.Race(new Resource(source.DefName), out var race);
            var hunt = row.Kind as RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Hunt;
            var offered = new AcquisitionSource { Observed = row, Entity = source, Pest = race.Pest };
            if (hunt is not null)
            {
                // The race row's own flags and numbers: an inedible hunt is only ever a pest; the meat's
                // nutrition is the live MeatAmount stat times the meat def's Nutrition.
                offered = offered with { Predator = race.Predator };
                if (!raceKnown)
                {
                    throw new BridgeContractException($"hunt source {source.DefName} has no race row in the catalog");
                }
                offered = offered with { RevengeChance = race.ManhunterOnDamage };
                if (!race.Pest)
                {
                    // A race without meat yields no food.
                    var perUnit = race.Meat is RaceMeat.Yields { NutritionPerUnit: var per } ? per : 0;
                    offered = offered with { NutritionYield = Math.Max(0, hunt.MeatAmount) * perUnit };
                }
                offered = offered with { MeleeOnly = Meleeable(row.Source, hunt, tables.Pawns, race, offered) };
            }
            else
            {
                offered = offered with { Tree = catalog.PlantIsTree(source.DefName) };
                if (offered.Food)
                {
                    var (nutrition, shown) = catalog.ShownStatValue(row.Resource, "", CatalogStats.Nutrition);
                    if (shown)
                    {
                        offered = offered with { NutritionYield = row.Yield * (float)nutrition };
                    }
                }
            }
            if (hunt is not null)
            {
                offered = offered with { Products = race.Butchery };
                var verdict = census.Gate(new HuntPrey { Source = offered });
                if (verdict.Hold is { } held)
                {
                    holds.Add(held with { Source = offered });
                    continue;
                }
                offered = offered with { WeaponRange = verdict.WeaponRange };
            }
            rows.Add(offered);
        }
        return (EquatableArray.From(rows), holds);
    }

    /// <summary>
    /// The day-one interim food rule over the race row: safe prey (not in a mental state, its meat nourishing)
    /// that is no predator, and downed or docile (no manhunter chance on damage) and no bigger than a colonist,
    /// which flees rather than fights back, so a melee weapon or bare hands can run it down. Pawn.BodySize is the
    /// current life stage's bodySizeFactor (the prey's life stage index, the pawn row's) times the race's
    /// baseBodySize; a prey whose row or stage is not held is not known to be small, so not meleeable. The
    /// comparison is in float32, as the game's.
    /// </summary>
    private static bool Meleeable(RimGovernor.Brain.GameFacts.Common.Ref source, RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Hunt row, Pawns pawns, AnimalRace race, AcquisitionSource prey)
    {
        if (row.InMentalState || race.Predator || race.Meat is not RaceMeat.Yields)
        {
            return false;
        }
        if (prey.Downed)
        {
            return true;
        }
        if (race.ManhunterOnDamage != 0)
        {
            return false;
        }
        if (!pawns.TryGetRow(source, out var state) || state.AnimalState is not RimGovernor.Brain.GameFacts.Observations.PawnAnimalState.Animal { State: var animal })
        {
            return false;
        }
        var index = animal.LifeStageIndex;
        if (index < 0 || index >= race.LifeStages.Count)
        {
            return false;
        }
        return (float)race.LifeStages[index].BodySizeFactor * (float)race.BodySize <= 1.0f;
    }

    /// <summary>Decodes native's raw hunt facts.</summary>
    private static HuntCensus HuntCensusOf(RimGovernor.Brain.GameFacts.Observations.HuntCensus v, DefinitionCatalog catalog)
    {
        var hunters = new List<HuntHunter>();
        foreach (var h in v.Hunters)
        {
            hunters.Add(new HuntHunter
            {
                Observed = h,
                // The weapon's verbs and projectile are the def rows' (Catalog.HuntWeapon).
                Weapon = catalog.HuntWeapon(h.WeaponDef ?? ""),
            });
        }
        return new HuntCensus { Hunters = hunters };
    }

    internal static HashSet<string> Set(IEnumerable<string> values) => new(values, StringComparer.Ordinal);

    internal static void ApplyAcquisition(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v, Tables tables, DefinitionCatalog? catalog, ref ReviewFacts r)
    {
        var (sources, holds) = DecodeAcquisition(v, tables, catalog);
        r = r with { Acquisition = sources, HuntHolds = holds };
    }
}
