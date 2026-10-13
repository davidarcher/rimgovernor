using RimWorld;
using Verse;
using Domain = RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_haul.go, catalog_roof.go, catalog_conditions.go and catalog_clearance_rows.go: the goods a colonist works
// out of the map, the roof rules, the weather and game condition facts and the work of clearing a thing.

public sealed partial class DefinitionCatalog
{
    /// <summary>
    /// The goods a colonist works out of the map and what each costs in work: per natural-rock mineable def with a yield,
    /// the work of its hit points at the pick's rate and the yield it drops; per tree def with a harvest, its harvest work
    /// and yield. The defs of one yielded item average. A mineable or tree whose numbers the catalog does not hold adds
    /// nothing (unknown, never zero); a catalog without the four stats leaves the model unknown.
    /// </summary>
    internal P.HaulModel HaulModel()
    {
        var game = P.PolicyFunctions.HaulGame();
        var stat = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var name in new[] { game.MiningSpeed, game.MiningYield, game.PlantWorkSpeed, game.PlantHarvestYield })
        {
            if (DefRow<StatDef>(name) is not { defaultBaseValue: > 0 } row)
            {
                return new P.HaulModel();
            }

            stat[name] = row.defaultBaseValue;
        }

        var mined = new Dictionary<P.Resource, List<P.HaulSource>>();
        var chopped = new Dictionary<P.Resource, List<P.HaulSource>>();
        foreach (var row in _thingDefs.Values)
        {
            if (row.building is { mineableThing: { } thing, isNaturalRock: true, mineableYield: > 0 } building
                && StatBaseEntry(row, PlanningStatNames.MaxHitPoints) is { } hp and > 0)
            {
                var units = Math.Max(1, building.mineableYield) * (double)building.mineableDropChance;
                if (building.mineableYieldWasteable)
                {
                    units *= stat[game.MiningYield];
                }

                var work = Math.Ceiling(hp / game.NaturalHit) * game.PickTicks / stat[game.MiningSpeed];
                var good = new P.Resource(thing.defName);
                Group(mined, good).Add(new P.HaulSource { Good = good, Units = units, Work = work });
            }

            if (row.plant is { harvestedThingDef: { } harvested, harvestWork: > 0, harvestYield: > 0 } plant && plant.IsTree && !plant.isStump)
            {
                var good = new P.Resource(harvested.defName);
                Group(chopped, good).Add(new P.HaulSource { Good = good, Units = plant.harvestYield * stat[game.PlantHarvestYield], Work = plant.harvestWork / stat[game.PlantWorkSpeed] });
            }
        }

        return new P.HaulModel { Mined = AverageHaulSources(mined), Chopped = AverageHaulSources(chopped) };
    }

    private static List<P.HaulSource> Group(Dictionary<P.Resource, List<P.HaulSource>> by, P.Resource good)
    {
        if (!by.TryGetValue(good, out var list))
        {
            by[good] = list = [];
        }

        return list;
    }

    // One source a good: the mean of its defs' yields and work, sorted by good.
    private static List<P.HaulSource> AverageHaulSources(Dictionary<P.Resource, List<P.HaulSource>> byGood) =>
        [.. byGood
            .Select(pair => new P.HaulSource { Good = pair.Key, Units = pair.Value.Average(s => s.Units), Work = pair.Value.Average(s => s.Work) })
            .OrderBy(s => s.Good.Value, StringComparer.Ordinal)];

    /// <summary>
    /// Every RoofDef row of the catalog as policy rules (thick roofs cannot be removed, natural ones are mountain roofs).
    /// A catalog with no RoofDef rows is an error: every game has at least its own roofs, so none means the defs were not read.
    /// </summary>
    public P.RoofRules RoofRules()
    {
        var roofs = Rows<RoofDef>();
        return roofs.Count == 0
            ? throw new CatalogException("roof rules: the catalog has no RoofDef rows")
            : new P.RoofRules(roofs.ToDictionary(pair => pair.Key, pair => new P.RoofRule { Thick = pair.Value.isThickRoof, Natural = pair.Value.isNatural }, StringComparer.Ordinal));
    }

    /// <summary>The WeatherDef's accuracyMultiplier: the factor on every ranged hit chance while the weather lasts. A weather the catalog has no row for and a non-positive multiplier are errors.</summary>
    public double WeatherAccuracy(string name)
    {
        var accuracy = (RequireRow<WeatherDef>(name)).accuracyMultiplier;
        return float.IsFinite(accuracy) && accuracy > 0 ? accuracy : throw new CatalogException($"weather {name} has accuracy multiplier {accuracy}");
    }

    /// <summary>Whether the game condition def's class is GameCondition_DisableElectricity or derives from it (every powered building is off while it lasts).</summary>
    public bool DisablesElectricity(string name) =>
        typeof(GameCondition_DisableElectricity).IsAssignableFrom(ConditionClass(RequireRow<GameConditionDef>(name)));

    /// <summary>
    /// Whether the biome's map conditions (the ones every map of the biome carries for its whole life) include one whose
    /// class is GameCondition_NoSunlight or derives from it: the sky never lights the ground, so plants that need light do
    /// not grow outdoors and an unroofed work cell stays dark by day. A biome the catalog has no row for is an error.
    /// </summary>
    public bool OutdoorsPermanentlyDark(string biome) =>
        RequireRow<BiomeDef>(biome).biomeMapConditions.Seq().Any(condition => typeof(GameCondition_NoSunlight).IsAssignableFrom(ConditionClass(condition)));

    private static Type ConditionClass(GameConditionDef condition) =>
        condition.conditionClass ?? throw new CatalogException($"game condition {condition.defName} names no class");

    /// <summary>
    /// The game's own work of removing a thing and what it leaves: a plant's harvestWork with its harvested yield, a
    /// building's deconstruct work (its WorkToBuild clamped to the JobDriver_Deconstruct limits) with the fraction of its
    /// cost the game refunds. Null for a def the catalog has no such row for.
    /// </summary>
    public P.ClearRow? ClearRow(string def, string stuff)
    {
        if (FindThing(def) is not { } thing)
        {
            return null;
        }

        if (thing.plant is { } plant)
        {
            double harvestWork = plant.harvestWork;
            if (!double.IsFinite(harvestWork) || harvestWork < 0)
            {
                return null;
            }

            Domain.EquatableArray<P.Amount> yield = plant.harvestedThingDef is { } harvested && plant.harvestYield > 0
                ? new([new P.Amount(new P.Resource(harvested.defName), (long)Math.Round((double)plant.harvestYield, MidpointRounding.AwayFromZero))])
                : default;
            return new P.ClearRow { Work = harvestWork, Yield = yield };
        }

        var (work, shown) = ShownStatValue(def, stuff, StatNames.WorkToBuild);
        if (!shown)
        {
            return null;
        }

        var refund = (double)thing.resourcesFractionWhenDeconstructed;
        var refunded = AdjustedCosts(def, stuff)
            .Select(cost => new P.Amount(new P.Resource(cost.Def), (long)Math.Floor(cost.Units * refund)))
            .Where(amount => amount.Count > 0)
            .ToList();
        return new P.ClearRow
        {
            Work = Math.Min(Math.Max((double)work, GameConstants.MinDeconstructWork), GameConstants.MaxDeconstructWork),
            Yield = new([.. refunded]),
        };
    }
}
