using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3868: the catalog's stock recipes, what makes a part an install recipe puts back, and the production way from the defs to a
// replacement's wait: a fresh colony without the bench waits on trade, which is named; one with it crafts the part at its rate.
public sealed class StockArrivalCatalogTests
{
    private static readonly StatDef Potency = new() { defName = "MedicalPotency" };
    private static readonly StatDef Labor = new() { defName = "GeneralLaborSpeed" };
    private static readonly SkillDef CraftingSkill = new() { defName = "Crafting" };
    private static readonly WorkTypeDef Crafting = new() { defName = "Crafting" };
    private static readonly ThingDef Plasteel = new() { defName = "Plasteel" };
    private static readonly ThingDef Herbal = new() { defName = "MedicineHerbal", statBases = [new RimWorld.StatModifier { stat = Potency, value = 0.6f }] };
    private static readonly ThingDef BionicArm = new() { defName = "BionicArm" };
    private static readonly BodyPartDef Arm = new() { defName = "Arm" };

    private static IngredientCount Slot(ThingDef def, float count)
    {
        var filter = new ThingFilter();
        filter.SetAllow(def, true);
        var slot = new IngredientCount { filter = filter };
        slot.SetBaseCount(count);
        return slot;
    }

    private static DefinitionCatalog Catalog()
    {
        var install = new RecipeDef
        {
            defName = "InstallBionicArm", workerClass = typeof(Recipe_InstallArtificialBodyPart), appliedOnFixedBodyParts = [Arm],
            ingredients = [Slot(BionicArm, 1), Slot(Herbal, 2)],
        };
        var make = new RecipeDef
        {
            defName = "Make_BionicArm", workAmount = 10_000, products = [new ThingDefCountClass(BionicArm, 1)], ingredients = [Slot(Plasteel, 15)],
            workSpeedStat = Labor, skillRequirements = [new Verse.SkillRequirement { skill = CraftingSkill, minLevel = 8 }],
        };
        var bench = new ThingDef
        {
            defName = "FabricationBench", category = Verse.ThingCategory.Building, thingClass = typeof(Building_WorkTable),
            designationCategory = new DesignationCategoryDef { defName = "Production" }, recipes = [make],
        };
        return new DefinitionCatalog(
            [new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }, Potency, Labor, CraftingSkill, Crafting, Plasteel, Herbal, BionicArm, Arm,
                install, make, bench,
                new WorkGiverDef { defName = "DoBillsFabricate", workType = Crafting, giverClass = typeof(WorkGiver_DoBill), fixedBillGiverDefs = [bench] }],
            DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));
    }

    [Fact]
    public void TheStockRecipesAreThoseMakingAnInstallRecipesPartAtTheBenchesItsBillGiverServes()
    {
        var recipe = Assert.Single(Catalog().StockRecipes(new HashSet<string>()));
        Assert.Equal(("Make_BionicArm", new Resource("BionicArm"), 1L, 10_000.0, "GeneralLaborSpeed", StockArrivalRules.BenchSpeedStat),
            (recipe.Recipe, recipe.Product, recipe.Batch, recipe.WorkAmount, recipe.WorkSpeedStat, recipe.BenchSpeedStat));
        Assert.Equal([new StockRecipeBench("FabricationBench", WorkType.Crafting)], recipe.Benches);
        Assert.Equal([new Policy.SkillRequirement("Crafting", 8)], recipe.Skills);
        Assert.Equal([[new Amount(new Resource("Plasteel"), 15)]], recipe.Ingredients);
    }

    [Fact]
    public void AFreshColonyWithoutTheBenchWaitsOnTradeAndOneWithItCraftsThePartAtItsRate()
    {
        // The production way (#3822): the catalog's recipes, the bills census's benches and a crafting colonist as the read boundary
        // fills them, to the replacement the autopilot would book for a lost arm with no arm on the map.
        var recipes = Catalog().StockRecipes(new HashSet<string>());
        StockCrafter[] crafters = [new("Anna", 8, [WorkType.Crafting], [new SkillLevel("Crafting", 10)], [new WorkSpeedRead("GeneralLaborSpeed", 1)])];
        var stock = new Dictionary<Resource, long> { [new Resource("Plasteel")] = 100 };
        var replacement = new OrganReplacementRead("Arm", "InstallBionicArm")
        {
            PartThing = "BionicArm", PartOnMap = false, MedicineOnMap = true, MedicineCount = 2, EligibleDoctors = 1, SuccessChance = 0.95, DoctorSuccessChance = 0.95,
        };

        var fresh = StockArrivalRules.Craft(recipes, [], crafters, stock) with { Medicines = [new Resource("MedicineHerbal")] };
        Assert.Empty(fresh.Sources);
        Assert.Equal([StockArrival.TradeUnmodelled], fresh.Findings);
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var (pick, reason, wait) = SurgeryRules.RestorePick([(replacement, 1.0)], fresh, 30);
        StockWait.Name(fresh, reason, wait, findings);
        Assert.Equal((null, SurgeryWantReason.PartShort), (pick, reason));
        Assert.Contains(StockArrival.TradeUnmodelled, findings);

        // A fabrication bench at 1x: 8 hours × 2500 ticks ÷ 10 000 work, 2 arms a day; 100 plasteel pays for 6.
        var built = StockArrivalRules.Craft(recipes, [new StockBench("FabricationBench", true, 1)], crafters, stock) with { Medicines = [new Resource("MedicineHerbal")] };
        var source = Assert.Single(built.Sources);
        Assert.Equal((new Resource("BionicArm"), 2.0, 6L), (source.Resource, source.PerDay, source.Units));
        (pick, reason, wait) = SurgeryRules.RestorePick([(replacement, 1.0)], built, 30);
        Assert.Equal("InstallBionicArm", pick!.Recipe);
        Assert.Equal(0.5, wait!.Expected(30), 12);
    }
}
