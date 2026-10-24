using RimWorld;
using Verse;
using Verse.AI;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The work a pawn's value reads from the defs (#3825): which work type and speed stat a coupled task's work runs under, and
// the work a unit of nutrition costs to grow and cook.

public sealed partial class DefinitionCatalog
{
    /// <summary>
    /// The speed stat each job driver's work toil multiplies its progress by, per driver class (its subclasses inherit it), as
    /// the decompiled drivers read it: JobDriver_ConstructFinishFrame, JobDriver_RemoveBuilding (Deconstruct, Uninstall) and
    /// JobDriver_AffectRoof (BuildRoof, RemoveRoof) <c>ConstructionSpeed</c> times 1.7, JobDriver_RemoveFloor its SpeedStat
    /// <c>ConstructionSpeed</c>, JobDriver_PlantWork (Harvest, CutPlant) <c>PlantWorkSpeed</c> times lerp(3.3, 1, growth),
    /// JobDriver_PlantSow <c>PlantWorkSpeed</c>, JobDriver_Mine <c>MiningSpeed</c>, JobDriver_Research <c>ResearchSpeed</c> times
    /// the bench's ResearchSpeedFactor, JobDriver_TendPatient <c>MedicalTendSpeed</c> (600 ticks over it) and JobDriver_CleanFilth
    /// <c>CleaningSpeed</c>. A recipe's work runs at its own <c>RecipeDef.workSpeedStat</c> (Toils_Recipe.DoRecipeWork).
    /// </summary>
    private static readonly (Type Driver, string Stat)[] DriverSpeedStats =
    [
        (typeof(JobDriver_ConstructFinishFrame), nameof(StatDefOf.ConstructionSpeed)),
        (typeof(JobDriver_RemoveBuilding), nameof(StatDefOf.ConstructionSpeed)),
        (typeof(JobDriver_AffectRoof), nameof(StatDefOf.ConstructionSpeed)),
        (typeof(JobDriver_RemoveFloor), nameof(StatDefOf.ConstructionSpeed)),
        (typeof(JobDriver_PlantWork), nameof(StatDefOf.PlantWorkSpeed)),
        (typeof(JobDriver_PlantSow), nameof(StatDefOf.PlantWorkSpeed)),
        (typeof(JobDriver_Mine), nameof(StatDefOf.MiningSpeed)),
        (typeof(JobDriver_Research), nameof(StatDefOf.ResearchSpeed)),
        (typeof(JobDriver_TendPatient), nameof(StatDefOf.MedicalTendSpeed)),
        (typeof(JobDriver_CleanFilth), nameof(StatDefOf.CleaningSpeed)),
    ];

    /// <summary>
    /// The stats a pawn's value reads per pawn: every mapped job driver's and every recipe's work speed (#3825), and the move speed
    /// and carrying capacity its food's hauling walks and carries at (#3849), by name.
    /// </summary>
    public static IReadOnlyList<string> UpkeepStatNames(IEnumerable<RecipeDef> recipes) =>
        [.. DriverSpeedStats.Select(d => d.Stat).Concat(recipes.Select(r => r.workSpeedStat?.defName).OfType<string>())
            .Append(nameof(StatDefOf.MoveSpeed)).Append(nameof(StatDefOf.CarryingCapacity))
            .Distinct().Order(StringComparer.Ordinal)];

    /// <summary>
    /// The work-type and speed-stat map of the work shortfall (#3825, <see cref="P.WorkSpeedFacts"/>): per WorkGiverDef its
    /// WorkTypeDef, per JobDef whose driver class <see cref="DriverSpeedStats"/> maps its stat, per RecipeDef its workSpeedStat
    /// ("" for none: the recipe works at 1) and the hauling work type.
    /// </summary>
    public P.WorkSpeedFacts WorkSpeedFacts()
    {
        var givers = Rows<WorkGiverDef>().Values.Where(g => g.workType is not null)
            .ToDictionary(g => g.defName, g => g.workType.defName, StringComparer.Ordinal);
        var jobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var job in Rows<JobDef>().Values)
        {
            if (job.driverClass is { } driver && DriverSpeedStats.FirstOrDefault(d => d.Driver.IsAssignableFrom(driver)) is { Driver: not null } mapped)
            {
                jobs[job.defName] = mapped.Stat;
            }
        }

        var recipes = Rows<RecipeDef>().Values.ToDictionary(r => r.defName, r => r.workSpeedStat?.defName ?? "", StringComparer.Ordinal);
        return new P.WorkSpeedFacts
        {
            GiverWork = givers, JobStat = jobs, RecipeStat = recipes, Hauling = nameof(WorkTypeDefOf.Hauling),
        };
    }

    /// <summary>
    /// The work a unit of nutrition costs the colony (#3825, <see cref="P.FoodWorkFacts"/>), at speed 1, over the crops and meals the
    /// colony can make with <paramref name="finished"/> research: the sowable crop of a human-edible harvest with the least
    /// (sowWork + harvestWork) / (harvestYield x the harvest's Nutrition), and the meal recipe (one IsMeal product, ingredients counted
    /// by IngredientValueGetter_Nutrition) with the least work per nutrition made, with the ingredient nutrition it takes per nutrition
    /// made. With no such recipe the colony eats its crop raw (no cooking); with no such crop there is nothing to grow food with.
    /// Its hauling (#3849): the harvest and the meal as carried (Nutrition, stackLimit, VolumePerUnit), whether the harvest rots
    /// (CompProperties_Rottable), the meal recipe's product count, its benches (recipeUsers and every ThingDef listing it in its
    /// recipes, as RecipeDef.AllRecipeUsers) and the move and carry stats.
    /// </summary>
    public P.FoodWorkFacts FoodWork(IReadOnlySet<string> finished)
    {
        var nutrition = ItemFacts().Nutrition;
        double Nutrition(ThingDef def) => nutrition.TryGetValue(new P.Resource(def.defName), out var n) ? n : 0;
        bool Researched(IEnumerable<ResearchProjectDef>? prerequisites) => (prerequisites ?? []).All(r => finished.Contains(r.defName));
        var crop = _thingDefs.Values
            .Where(t => t.plant is { Sowable: true, Harvestable: true, harvestedThingDef: { ingestible.HumanEdible: true } } plant && !plant.IsTree
                && Nutrition(plant.harvestedThingDef) > 0 && Researched(plant.sowResearchPrerequisites))
            .Select(t => (t.defName, Ticks: (t.plant.sowWork + t.plant.harvestWork) / (t.plant.harvestYield * Nutrition(t.plant.harvestedThingDef)), Harvest: t.plant.harvestedThingDef))
            .OrderBy(c => c.Ticks).ThenBy(c => c.defName, StringComparer.Ordinal).FirstOrDefault();
        var meal = Rows<RecipeDef>().Values
            .Where(r => r.products is [{ thingDef.ingestible.IsMeal: true } product] && product.count > 0 && Nutrition(product.thingDef) > 0
                && r.workAmount > 0 && r.ingredients.Count > 0 && r.IngredientValueGetter is IngredientValueGetter_Nutrition
                && Researched(r.researchPrerequisite is null ? r.researchPrerequisites : [r.researchPrerequisite, .. r.researchPrerequisites ?? []]))
            .Select(r => (r.defName, Made: r.products[0].count * Nutrition(r.products[0].thingDef), r.workAmount, Ingredients: r.ingredients.Sum(i => (double)i.GetBaseCount()), Recipe: r))
            .OrderBy(m => m.workAmount / m.Made).ThenBy(m => m.defName, StringComparer.Ordinal).FirstOrDefault();
        P.CarriedFood Carried(ThingDef def) => new(Nutrition(def), def.stackLimit, def.VolumePerUnit);
        string[] benches = meal.Recipe is not { } recipe ? []
            : (recipe.recipeUsers ?? []).Concat(_thingDefs.Values.Where(t => t.recipes?.Contains(recipe) == true))
                .Select(t => t.defName).Distinct().Order(StringComparer.Ordinal).ToArray();
        return new P.FoodWorkFacts
        {
            Crop = crop.defName ?? "", GrowTicksPerNutrition = crop.defName is null ? 0 : crop.Ticks,
            Meal = meal.defName ?? "", CookTicksPerNutrition = meal.defName is null ? 0 : meal.workAmount / meal.Made,
            IngredientPerNutrition = meal.defName is null ? 1 : meal.Ingredients / meal.Made,
            Harvest = crop.Harvest is null ? null : Carried(crop.Harvest),
            HarvestRots = crop.Harvest?.GetCompProperties<CompProperties_Rottable>() is not null,
            Dish = meal.Recipe is null ? null : Carried(meal.Recipe.products[0].thingDef),
            DishesPerRun = meal.Recipe is null ? 0 : meal.Recipe.products[0].count,
            Benches = EquatableArray.From(benches),
            MoveStat = nameof(StatDefOf.MoveSpeed), CarryStat = nameof(StatDefOf.CarryingCapacity),
        };
    }
}
