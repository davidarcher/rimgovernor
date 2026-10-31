using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FacilityPrerequisiteTests
{
    [Fact]
    public void DefinitionsKeepFirstFailureAndRejectNonzeroSkillWithoutReadingWorkers()
    {
        PlanningDefinition ready = new() { Name = "ready", Available = true };
        PlanningDefinition gated = new() { Name = "gated", Research = ["r2", "r1"] };
        Assert.Equal(new FacilityEligibility(Cause.AwaitingPlan, "research", "r2+r1"), FacilityPlanningRules.DefinitionsGate([ready, gated], ["ready", "gated", "absent"]));
        Assert.Equal(new FacilityEligibility(Cause.FieldUnavailable, "absent_definition"), FacilityPlanningRules.DefinitionsGate([ready], ["absent", "ready"]));
        Assert.Equal(new FacilityEligibility(Cause.NoWorker, "builder_for_ready", "construction_skill_-1"), FacilityPlanningRules.DefinitionsGate([ready with { ConstructionSkill = -1 }], ["ready"]));
        Assert.Equal(default, FacilityPlanningRules.DefinitionsGate([ready], ["ready"]));
        Assert.Equal(new FacilityEligibility(Cause.AwaitingPlan, "gated", "unavailable"), FacilityPlanningRules.DefinitionAvailability(gated with { Research = [] }));
    }

    [Theory]
    [InlineData(4, 4, IndoorSleepingMethod.NoDeficit, 0)]
    [InlineData(3, 5, IndoorSleepingMethod.NoDeficit, 0)]
    [InlineData(5, 3, IndoorSleepingMethod.Build, 2)]
    [InlineData(64, 0, IndoorSleepingMethod.Build, 64)]
    [InlineData(65, 0, IndoorSleepingMethod.Bound, 0)]
    public void IndoorSleepingPreservesDeficitAndBound(long count, long capacity, IndoorSleepingMethod method, long wanted) =>
        Assert.Equal((method, wanted), FacilityPlanningRules.IndoorSleepingDemand(count, capacity));

    [Theory]
    [InlineData("Beer", 0, WorkshopPrerequisite.Barrel)]
    [InlineData("Beer", 1, WorkshopPrerequisite.Wort)]
    [InlineData("Steel", 0, WorkshopPrerequisite.Product)]
    public void OnlyBeerNeedsTheBarrelThenWort(string product, long barrels, WorkshopPrerequisite prerequisite) =>
        Assert.Equal(prerequisite, FacilityPlanningRules.WorkshopPrerequisiteOf(new(product), barrels, "Beer"));

    [Fact]
    public void ShellDefinitionsKeepMissingBeforeAvailability()
    {
        var unavailable = new PlanningDefinition { Name = "wall" };
        var available = unavailable with { Available = true };
        Assert.Equal(new FacilityEligibility(Cause.FieldUnavailable, "wall_door_definitions"), FacilityPlanningRules.ShellDefinitionsGate(unavailable, null));
        Assert.Equal(new FacilityEligibility(Cause.FieldUnavailable, "wall_door_availability"), FacilityPlanningRules.ShellDefinitionsGate(unavailable, available));
        Assert.Equal(default, FacilityPlanningRules.ShellDefinitionsGate(available, available));
    }

    [Fact]
    public void SuiteNeedsItsWholeRingAndOtherRoomsDoNotReadStock()
    {
        var suite = new PlannedRoom { Role = PlannedRole.Suite, Region = RegionRules.RectRegion(new Rectangle(0, 0, 3, 4)) };
        Assert.True(FacilityPlanningRules.SuiteStockAvailable(new(), suite with { Role = PlannedRole.Bedroom }));
        var facts = WorkResourceTestFacts.Create() with
        {
            Definitions = [new PlanningDefinition { Name = ContainmentCellRules.ShellWallDefinition, StuffOptions = [new StuffOption { Stuff = "Rice", Costs = [new(new("Rice"), 1)] }] }],
        };
        Assert.False(FacilityPlanningRules.SuiteStockAvailable(facts, suite));
        facts = facts with { Census = facts.Census with { Resources = [new() { DefName = "Rice", Units = 18 }] } };
        Assert.True(FacilityPlanningRules.SuiteStockAvailable(facts, suite));
        Assert.Throws<RequiredFactException>(() => FacilityPlanningRules.SuiteStockAvailable(new(), suite));
    }

    [Fact]
    public void VentUsesFirstLiveOrientedSiteAndKeepsNorthWithoutOne()
    {
        var cell = new Cell(2, 3);
        var plan = new LayoutPlan { Sites = [new() { Node = "a", Kind = NodeKind.Vent, Origin = cell }, new() { Node = "b", Kind = NodeKind.Vent, Origin = cell, Rotation = new Heading.Toward(Rotation.West) }, new() { Node = "c", Kind = NodeKind.Vent, Origin = cell, Rotation = new Heading.Toward(Rotation.East) }] };
        Assert.Equal(Rotation.West, FacilityPlanningRules.VentRotation(plan, cell));
        Assert.Equal(Rotation.North, FacilityPlanningRules.VentRotation(plan, new(8, 8)));
    }

    [Fact]
    public void SleepingPrerequisitesKeepRequiredReadsAndAbsentRoyaltyShortCircuit()
    {
        Assert.Empty(SleepingPlanningRules.TitleClaimQuests(new(), null));
        Assert.False(SleepingPlanningRules.ThroneNeed(new()).Owed);
        Assert.Throws<RequiredFactException>(() => SleepingPlanningRules.RoomUpgradeOf(new(), [], null));
        Assert.Throws<RequiredFactException>(() => SleepingPlanningRules.ThroneStepOf(new(), null));
        var facts = WorkResourceTestFacts.Create();
        Assert.False(SleepingPlanningRules.BedroomsOwedOf(facts, [], null));
        Assert.False(SleepingPlanningRules.CompanionBed(facts, null).Due);
        Assert.Throws<RequiredFactException>(() => SleepingPlanningRules.TitleFurniture(facts, null));
        Assert.Throws<RequiredFactException>(() => SleepingPlanningRules.RoomUpgradeOf(facts, [], null));
    }

    [Fact]
    public void ForbiddenSafeKitOnlyBlocksASelectedMaterialWithoutStock()
    {
        var loot = new LootItem
        {
            Entity = new() { Id = "kit", DefName = "Rice", Label = "kit", Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(1, new(0, 0)) },
            Observed = new() { Item = new() { Id = "kit" }, Forbidden = true, Safety = new RimGovernor.Brain.GameFacts.Observations.HaulSafety.Safe { PathLength = 1 }, Count = 1, StorageHeadroom = 0, AcceptedBy = [] },
        };
        var facts = WorkResourceTestFacts.Create() with { EventLoot = [loot] };
        Assert.True(FacilityPlanningRules.UnstockedUntilKitAllowed(facts, "missing", "Rice"));
        Assert.False(FacilityPlanningRules.UnstockedUntilKitAllowed(facts, "", "Rice"));
        Assert.False(FacilityPlanningRules.UnstockedUntilKitAllowed(facts with { EventLoot = [loot with { Observed = loot.Observed with { Forbidden = false } }] }, "missing"));
        Assert.False(FacilityPlanningRules.UnstockedUntilKitAllowed(facts with { EventLoot = [loot with { Observed = loot.Observed with { Safety = new RimGovernor.Brain.GameFacts.Observations.HaulSafety.Unsafe() } }] }, "missing"));
    }

    [Fact]
    public void BedroomDeficitPrecedesLaterConstructionAndWindowReads()
    {
        PawnId pawn = new("a");
        var hall = new PlannedRoom { Id = 1, Region = RegionRules.RectRegion(new Rectangle(0, 0, 5, 5)) };
        var bedroom = new PlannedRoom { Id = 2, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new Rectangle(10, 0, 3, 4)) };
        var original = WorkResourceTestFacts.Create("a");
        var facts = new ReviewFacts
        {
            Census = original.Census, ForecastObserved = original.ForecastObserved,
            Upkeep = new() { Rooms = RoomFixtures.Census((RoomFixtures.State("hall", RoomRole.Barracks, beds: ["s1"]), hall.Region.Cells())) },
            LayoutPlan = new() { Rooms = [bedroom], NextRoomId = 3 },
            Sleeping = new()
            {
                Colonists = 1,
                People = [new SleepingPerson { Native = SleepingNativeFixtures.Person(id: pawn), Pawn = SleepingNativeFixtures.Pawn(id: pawn, ownedBed: "s1") }],
                Beds = [new SleepingBed { Native = SleepingNativeFixtures.Bed(id: "s1", room: "hall", owners: [pawn], accessibleTo: [pawn]), Building = SleepingNativeFixtures.Building(id: "s1", definition: new("Bed")), Humanlike = true }],
            },
        };
        Assert.True(SleepingPlanningRules.BedroomsOwedOf(facts, [], null));
    }
    private static GearBench Bench(string definition) => new() { Observed = ProductionBenchFixtures.Observed("bench", definition, "token", true), Bills = [], Recipes = [] };

    private static RecipeHost Host(string name, bool available, params string[] research) =>
        new() { Definition = "make-" + name, Products = [new(name)], Available = available, Benches = ["bench-" + name], Ingredients = [], Research = [.. research] };

    private static BenchDefinition Definition(string name, bool available = true, params string[] research) =>
        new() { Name = "bench-" + name, Available = available, Research = [.. research] };

    private static GearRecipe Recipe(string product, string recipe) => new()
    {
        Access = ProductionBenchFixtures.Recipe(recipe), Products = [new(product)], Ingredients = [], RequiredWork = [], WorkAmount = 1,
    };

    [Fact]
    public void AStandingBenchRecipeSettlesInBenchAndRecipeOrder()
    {
        IReadOnlyList<GearBench> benches = [Bench("first") with { Recipes = [Recipe("other", "skip"), Recipe("shirt", "first-recipe")] }, Bench("second") with { Recipes = [Recipe("shirt", "second-recipe")] }];
        Assert.Equal("first-recipe", WorkRules.StandingWorkshopRecipe(new("shirt"), benches));
        Assert.Null(WorkRules.StandingWorkshopRecipe(new("pants"), benches));
        Assert.Throws<DomainException>(() => WorkRules.StandingWorkshopRecipe(new(""), benches));
    }

    [Fact]
    public void AHostableProductWaitsOnTheComparisonAndNeverPicksABench()
    {
        RecipeHost[] hosts = [Host("shirt", true)];
        Assert.Equal(["bench-shirt"], WorkRules.WorkshopResearchCandidates(new("shirt"), hosts));
        // An undescribed bench definition is a missing fact, never a guess.
        Assert.Throws<RequiredFactException>(() => WorkRules.WorkshopHostable(new("shirt"), hosts, []));
        Assert.True(WorkRules.WorkshopHostable(new("shirt"), hosts, [Definition("shirt")]));
        Assert.Null(WorkRules.WorkshopResearchRung(new("shirt"), hosts, [Definition("shirt")]));
        Assert.False(WorkRules.WorkshopHostable(new("shirt"), hosts, [Definition("shirt", false, "tailoring")]));
    }

    [Fact]
    public void OnlyResearchStandingBetweenTheColonyAndAHostIsARung()
    {
        RecipeHost[] hosts = [Host("armor", false, "smithing")];
        var rung = WorkRules.WorkshopResearchRung(new("armor"), hosts, [Definition("armor", false, "electricity")]);
        Assert.NotNull(rung);
        Assert.Equal("bench-armor", rung.Bench);
        Assert.Equal("make-armor", rung.Recipe);
        Assert.Equal(["electricity", "smithing"], rung.Research);
        // A gate the census names no project for is no rung.
        Assert.Null(WorkRules.WorkshopResearchRung(new("armor"), [Host("armor", false)], [Definition("armor")]));
    }
}
