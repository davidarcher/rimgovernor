using RimGovernor.Brain.Policy;
using Xunit;
using Observations = RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PolicyFailurePropagationTests
{
    [Fact]
    public void CarryChangesDoNotDiscardARequiredSettingForAnInvalidPawn()
    {
        Assert.Throws<DomainException>(() => MedicineCarryRules.MedicineCarryChanges(
            [new MedicineCarryPawn { Id = new(""), Doctor = true, Care = MedicalCare.Normal }], 10, 0));
        Assert.Single(MedicineCarryRules.MedicineCarryChanges(
            [new MedicineCarryPawn { Id = new("doctor"), Doctor = true, Care = MedicalCare.Normal }], 10, 0));
    }

    [Fact]
    public void DuplicateNamesDoNotBecomeSatisfiedWhenARenameCannotBeWritten()
    {
        Observations.OwnedName first = new() { PawnId = new("first"), ThingId = 1, ShortName = "Alex" };
        var duplicate = first with { PawnId = new(""), ThingId = 2 };
        Assert.Throws<DomainException>(() => PawnRules.NicknameChanges([first, duplicate]));
        Assert.Single(PawnRules.NicknameChanges([first, duplicate with { PawnId = new("second") }]));
        Assert.Empty(PawnRules.NicknameChanges([first, duplicate with { ShortName = "Blair" }]));
    }

    [Fact]
    public void AnInvalidFundedDecreeRecipeDoesNotSilentlyChooseAnotherRecipe()
    {
        var due = new DecreeDue { Objective = QuestStateTests.Objective(Observations.QuestObjectiveKind.ProduceItem), Def = "product", Stuff = "", Remaining = 1 };
        var invalid = new DecreeRecipe { Bench = "", Recipe = "recipe", Product = new("product"), Units = 1, Available = true, Ingredients = [] };
        var valid = invalid with { Bench = "bench" };
        Assert.Throws<DomainException>(() => QuestDecrees.PlanDecreeProduction(due, [invalid, valid], []));
        Assert.NotNull(QuestDecrees.PlanDecreeProduction(due, [valid], []).Bill);
        Assert.Equal("production_infeasible", QuestDecrees.PlanDecreeProduction(due, [valid with { Available = false }], []).Reason);
    }

    [Fact]
    public void RockPlanningDistinguishesDoorlessRoomsFromBrokenDoorGeometry()
    {
        var room = new PlannedRoom { Region = RegionRules.RectRegion(new Rectangle(10, 10, 3, 3)), Door = new Cell(10, 10) };
        var plan = new LayoutPlan();
        Assert.NotNull(plan.RoomRock(room, []));
        Assert.Throws<DomainException>(() => plan.RoomRock(room with { DoorSide = new DoorSide.Doored(Rotation.North) }, []));
    }

    [Fact]
    public void ADisarmCatalogReadDoesNotHideAMissingInstallItemPrice()
    {
        var item = new Verse.ThingDef { defName = "implant" };
        var filter = new Verse.ThingFilter();
        filter.SetAllow(item, true);
        var ingredient = new Verse.IngredientCount { filter = filter };
        ingredient.SetBaseCount(1);
        var recipe = new Verse.RecipeDef
        {
            defName = "install", addsHediff = new Verse.HediffDef { defName = "installed" },
            appliedOnFixedBodyParts = [new Verse.BodyPartDef { defName = "part" }], ingredients = [ingredient],
        };
        var catalog = new RimGovernor.Brain.Bridge.DefinitionCatalog(
            [new Verse.ThingDef { defName = "Silver" }, new Verse.TerrainDef { defName = "Soil" }, item, recipe],
            RimGovernor.Brain.Bridge.DefStatTable.Empty, new RimGovernor.Brain.Bridge.CatalogDerived("Silver", "Wort", 10f));
        Assert.Throws<StatException>(() => catalog.CreepJoinerDisarm());
    }
}
