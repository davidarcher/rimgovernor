using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Bridge.Contracts;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using CatalogStats = RimGovernor.Brain.Bridge.CatalogStats;
using C = RimGovernor.Contracts.Wire.Common;
using D = RimGovernor.Contracts.Wire.Defs;
using O = RimGovernor.Contracts.Wire.Observations;
using Ops = RimGovernor.Contracts.Wire.Operations;
using PawnId = RimGovernor.Brain.Domain.PawnId;
using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Bridge;

public static class ColonyProductionReads
{
    /// <summary>The nutrition of the fresh humanlike corpses near a bench: each race's live MeatAmount sum times the Nutrition of that race's meat def; a race whose meat shows no nutrition adds none.</summary>
    private static double HumanCorpseNutrition(IEnumerable<O.HumanCorpseMeat> meat, DefinitionCatalog? catalog)
    {
        var defs = ColonyPlanningReads.RequireCatalog(catalog);
        var total = 0.0;
        foreach (var m in meat)
        {
            total += m.MeatAmount * (defs.RaceMeatNutrition(m.Race) ?? 0);
        }
        return total;
    }

    /// <summary>A growing zone's raw crop facts for policy: the planted plants' means are their totals over the planted count, and an unplanted zone has none.</summary>
    internal static CropGrowth FarmGrowth(O.ZoneState.Growing farm, IEnumerable<PlanningDefinition> definitions)
    {
        var plant = definitions.FirstOrDefault(d => d.Name == farm.Crop)?.Plant ?? throw new RequiredFactException("zone crop definition:" + farm.Crop);
        if (!double.IsFinite(farm.Temperature))
        {
            throw new RequiredFactException("zone temperature");
        }
        return new CropGrowth
        {
            Observed = farm,
            MinGrowth = plant.MinGrowthTemperature,
            MinOptimal = plant.MinOptimalGrowthTemperature,
            MaxOptimal = plant.MaxOptimalGrowthTemperature,
            MaxGrowth = plant.MaxGrowthTemperature,
        };
    }

    /// <summary>
    /// Computes each field's harvest lead in policy from the raw growth facts, the current temperature and the
    /// calendar. Fallow and dormant fields offer no arrival. A planted field is future capacity, not an observed
    /// delivery of edible stock. Exposure uses the native whole-zone roof and plant counts.
    /// </summary>
    internal static EquatableArray<FoodField> ColonyFoodFields(IReadOnlyList<O.ZoneState.Growing> farms, IReadOnlyList<PlanningDefinition> definitions, RimGovernor.Contracts.Wire.Observations.FoodClimate calendar)
    {
        var rows = new List<FoodField>();
        foreach (var farm in farms)
        {
            if (farm.Crop.Length == 0) continue;
            var definition = definitions.FirstOrDefault(d => d.Name == farm.Crop) ?? throw new RequiredFactException("zone crop definition:" + farm.Crop);
            var growth = FarmGrowth(farm, definitions);
            if (definition.Harvest is null) continue;
            var crop = definition.CropChoice();
            if (CalendarRules.HarvestLeadDays(growth, crop.GrowDays, calendar) is not HarvestTiming.Expected lead) continue;
            var growing = growth.GrowingCells();
            rows.Add(new FoodField
            {
                Observed = farm,
                Plan = new FieldPlan { Crop = crop, Sites = new FarmSitePlan { Cells = checked((int)growing) } },
                RemainingGrowDays = lead.Days,
                WorkPerDay = crop.HarvestWork * growing / crop.GrowDays,
            });
        }
        return EquatableArray.From(rows);
    }
    internal static EquatableArray<FieldCrop> ColonyFieldCrops(IReadOnlyList<O.ZoneState.Growing> farms, IReadOnlyList<PlanningDefinition> definitions, bool usable = false)
    {
        var rows = new List<FieldCrop>();
        foreach (var farm in farms)
        {
            if (farm.Crop.Length == 0) continue;
            var definition = definitions.FirstOrDefault(d => d.Name == farm.Crop) ?? throw new RequiredFactException("zone crop definition:" + farm.Crop);
            if (definition.Plant is null) throw new RequiredFactException("zone plant definition:" + farm.Crop);
            if (definition.Harvest is null) continue;
            var crop = definition.CropChoice();
            rows.Add(new FieldCrop { Edible = crop.Edible, GrowingCells = usable ? farm.UsableCells : FarmGrowth(farm, definitions).GrowingCells(), GrowDays = crop.GrowDays, HarvestNutrition = crop.HarvestNutrition, Demand = crop.Demand });
        }
        return EquatableArray.From(rows);
    }
    internal static RoundsFacts ColonyProduction(O.ColonyFactsSnapshot v, RoundsFacts facts)
    {
        var ready = false;
        foreach (var bench in v.ProductionBenches)
        {
            if (bench.Cooking is not O.CookingProduction.Supported cooking || !cooking.Usable)
            {
                continue;
            }
            var recipes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipe in cooking.Recipes)
            {
                recipes.Add(recipe.Recipe);
            }
            foreach (var bill in bench.Bench.Bills)
            {
                if (recipes.Contains(bill.Recipe) && !bill.Suspended)
                {
                    ready = true;
                }
            }
        }
        return facts with { Cooking = ready };
    }

    /// <summary>
    /// A recipe product: the frame's varying facts (whether a human eats it, storage room) joined to what its def row
    /// says (food kind, the Nutrition the game shows, rot, baby edibility). A def the catalog gives no food kind is
    /// no food; the game shows no Nutrition stat for a def that gives none, so its nutrition is 0.
    /// </summary>
    private static ProductionProduct ProductionProductOf(O.FoodProduct product, DefinitionCatalog? catalog)
    {
        var name = product.DefName;
        var defs = ColonyPlanningReads.RequireCatalog(catalog);
        if (defs.FoodKindOf(name) is not { } kind)
        {
            return new ProductionProduct { Food = new ProductFood.NotFood(product) };
        }
        var (nutrition, shown) = defs.ShownStatValue(name, "", CatalogStats.Nutrition);
        return new ProductionProduct
        {
            Food = new ProductFood.Food(product, kind, shown ? nutrition : 0, defs.RotDays(name) is not null, defs.BabyEdible(name)),
        };
    }

    internal static EquatableArray<ProductionBench> ColonyProductionBenches(O.ColonyFactsSnapshot v, Pawns pawns, DefinitionCatalog? catalog)
    {
        var rows = new List<ProductionBench>();
        ProductionBench Add(O.BillStackRow bench, ProductionUse use, IEnumerable<O.RecipeState> recipes, IEnumerable<O.BillState> bills, IEnumerable<O.FoodProduction> production, bool butcher)
        {
            var row = new ProductionBench
            {
                Observed = bench,
                Use = use,
            };
            var recipeRows = new List<ProductionRecipe>();
            foreach (var r in recipes)
            {
                var name = r.Recipe;
                var recipeCatalog = ColonyPlanningReads.RequireCatalog(catalog);
                var recipe = new ProductionRecipe { Access = new RecipeAccess.OnBench(r), Role = recipeCatalog.RecipeRoleOf(name), Bulk = recipeCatalog.RecipeBulk(name), Food = recipeCatalog.RecipeFood(name) };
                var products = new List<ProductionProduct>();
                foreach (var p in production)
                {
                    if (p.Recipe != name)
                    {
                        continue;
                    }
                    recipe = recipe with { Access = new RecipeAccess.OnBench(r, p) };
                    foreach (var product in p.Products)
                    {
                        products.Add(ProductionProductOf(product, catalog));
                    }
                }
                if (!butcher && recipe.Access is RecipeAccess.OnBench { Food: null })
                    throw new RequiredFactException("cooking product gates:" + name);
                recipeRows.Add(recipe with { Products = products });
            }
            var billRows = new List<ExistingProductionBill>();
            foreach (var b in bills)
            {
                billRows.Add(new() { Observed = b, Role = ColonyPlanningReads.RequireCatalog(catalog).RecipeRoleOf(b.Recipe), Butcher = butcher });
            }
            return row with { Recipes = recipeRows, Bills = billRows };
        }
        foreach (var bench in v.ProductionBenches)
        {
            if (bench.Cooking is O.CookingProduction.Supported cooking)
                rows.Add(Add(bench.Bench, new ProductionUse.Cooking(cooking), cooking.Recipes, bench.Bench.Bills, cooking.Production, false));
            if (bench.Butchering is not O.ButcheringProduction.Supported b) continue;
            var row = Add(bench.Bench, new ProductionUse.Butchering(b), b.Recipes, bench.Bench.Bills, [], true);
            var butchers = new List<HumanButcherCandidate>();
            foreach (var candidate in b.HumanButchers)
            {
                if (!pawns.TryGetValue(candidate.PawnId, out var pawn) || pawn is null)
                    throw new ObservationContractException($"human butcher {candidate.PawnId} has no pawn row");
                var traits = new List<PawnTrait>();
                foreach (var t in candidate.PawnTraits)
                {
                    traits.Add(RoundsWorkReads.ResolveTrait(catalog, t));
                }
                butchers.Add(new HumanButcherCandidate
                {
                    Observed = candidate,
                    Traits = EquatableArray.From(traits),
                    Ideology = pawn.Settings.PolicyInputs.Ideo,
                });
            }
            rows.Add(row with { HumanCorpseNutrition = HumanCorpseNutrition(b.HumanCorpseMeat, catalog), HumanButchers = butchers });
        }
        return EquatableArray.From(rows);
    }

    internal static RoundsFacts ZoneProduction(IReadOnlyList<O.ZoneState.Growing> farms, IReadOnlyList<PlanningDefinition> definitions, RoundsFacts facts)
    {
        var growing = farms.Where(farm => farm.EdibleCrop).Sum(farm => FarmGrowth(farm, definitions).GrowingCells());
        return facts with { GrowingCells = growing };
    }
}
