using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The worn-count goal (#3496): the garment joins the outfit requirements of the first eligible colonists, its material
/// joins the clothing runway and the stuff's research joins the research-wanted set.
/// </summary>
public sealed class WornCountGoalTests
{
    private const string Duster = "Apparel_Duster";
    private static readonly Resource Devilstrand = new("DevilstrandCloth");

    private static GearPawn Pawn(string id, GearRoleInput? role = null, bool canWear = true, bool wearing = false, bool blocked = false) => new() { Observed = GearFixtures.Loadout("") with { Token = "", Pawn = new(id), Blocked = blocked }, Policy = new ApparelPolicyState { Observed = PoliciesTests.EmptyOutfit, Role = role ?? GearFixtures.Role, Definitions = canWear ? [new() { Name = Duster, Adult = true, CoversBody = true }, new() { Name = "Apparel_Shirt", Adult = true, CoversBody = true }] : [new() { Name = "Apparel_Shirt", Adult = true, CoversBody = true }] }, LoadoutModel = new GearModel.Modeled(new GearLoadoutInput { Observed = GearFixtures.Loadout(""), Body = EmergencyFixtures.Placed("").Settings.Body with { ComfortableMinC = 0, ComfortableMaxC = 0 }, Threat = ColonyModelFixtures.NoThreat, Worn = wearing ? [new GearOption { Origin = new GearOptionOrigin.Observed(GearFixtures.Option("", Duster, (Devilstrand).Value, GearSource.Worn) with { Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Awful) }) }] : [], Role = GearFixtures.Role }) };

    private static ReviewFacts Facts(params GearPawn[] pawns) => new()
    {
        Gear = ColonyModelFixtures.NoGear() with { Pawns = pawns },
        Census = ColonyFactsCoreTests.Snapshot() with { Resources = [] },
        Items = new ItemFacts { StuffCategories = new Dictionary<Resource, IReadOnlyList<string>> { [Devilstrand] = ["Fabric"] } },
        Garments = [new ClothingGarment { Definition = new(Duster), Groups = ["Torso"], Slots = [[new Amount(Devilstrand, 60)]] }],
    };

    private static GoalRecord Goal(int count = 2) => new() { Id = 1, Kind = GoalKind.WornCount, Target = Duster, Qualifier = Devilstrand.Value, Count = count };

    private static GoalsFacts GoalFacts(ReviewFacts rounds, params string[] finished) => new()
    {
        Goals = [Goal()],
        ReviewState = rounds,
        Research = ResearchTestFacts.Read(finished: finished),
        StuffResearch = new Dictionary<string, IReadOnlyList<string>> { [Devilstrand.Value] = ["Devilstrand"] },
    };

    private static IReadOnlyList<string> Required(ReviewFacts f) =>
        [.. f.Gear.Pawns.Select(p => string.Join(",", p.Policy!.Required.Items))];

    [Fact]
    public void TheGarmentIsRequiredForTheFirstEligibleColonistsByPawnId()
    {
        var rounds = Facts(
            Pawn("p4"),
            Pawn("p1"),
            Pawn("p2", new GearRoleInput { Observed = GearFixtures.Loadout("") with { ApparelPolicy = PoliciesTests.EmptyOutfit }, Pawn = EmergencyFixtures.Placed("") with { Drafted = true } }),
            Pawn("p3", new GearRoleInput { Observed = GearFixtures.Loadout("") with { ApparelPolicy = PoliciesTests.EmptyOutfit, DevelopmentalStage = (true) ? RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage.Child : RimGovernor.Brain.GameFacts.Defs.DevelopmentalStage.Adult }, Pawn = PawnFixtures.Row("") }),
            Pawn("p5", canWear: false),
            Pawn("p6"));
        var stamped = GoalRules.Stamp(rounds, GoalRules.DemandOf(GoalFacts(rounds)));
        // p4, p1 and p6 are eligible; the first two by id are p1 and p4. The soldier, the child and the pawn who cannot wear it get nothing.
        Assert.Equal(["p1", "p4"], stamped.Gear.Pawns.Where(p => p.Policy!.Required.Items.Contains(Duster)).Select(p => p.Pawn.Value).Order());
    }

    [Fact]
    public void TheMissingGarmentsJoinTheClothingRunway()
    {
        var rounds = Facts(Pawn("p1", wearing: true), Pawn("p2"), Pawn("p3"));
        var stamped = GoalRules.Stamp(rounds, GoalRules.DemandOf(GoalFacts(rounds)));
        // Two goal, one already worn: one duster missing at 60 devilstrand.
        Assert.Equal(60, stamped.ClothingRunway().Needs[Devilstrand]);
        Assert.Empty(rounds.ClothingRunway().Needs);
    }

    [Fact]
    public void AMetGoalOwesNoMaterialAndKeepsTheRequirement()
    {
        var rounds = Facts(Pawn("p1", wearing: true), Pawn("p2", wearing: true));
        var facts = GoalFacts(rounds);
        Assert.Equal(GoalState.Met, GoalRules.Evaluate(Goal(), facts).State);
        var stamped = GoalRules.Stamp(rounds, GoalRules.DemandOf(facts));
        Assert.Empty(stamped.ClothingRunway().Needs);
        Assert.Equal(2, stamped.Gear.Pawns.Count(p => p.Policy!.Required.Items.Contains(Duster)));
    }

    [Fact]
    public void StampingTwiceChangesNothing()
    {
        var rounds = Facts(Pawn("p1"), Pawn("p2"));
        var demand = GoalRules.DemandOf(GoalFacts(rounds));
        var once = GoalRules.Stamp(rounds, demand);
        var twice = GoalRules.Stamp(once, demand);
        Assert.Equal(Required(once), Required(twice));
        Assert.Equal(once.ClothingRunway().Needs, twice.ClothingRunway().Needs);
    }

    [Fact]
    public void AnUnresearchedStuffPutsItsProjectInTheResearchWantedSet()
    {
        var rounds = Facts(Pawn("p1"), Pawn("p2"));
        Assert.Equal(["Devilstrand"], GoalRules.DemandOf(GoalFacts(rounds)).Research);
        Assert.Empty(GoalRules.DemandOf(GoalFacts(rounds, "Devilstrand")).Research);
    }

    [Fact]
    public void StuffResearchComesFromThePlantsThatHarvestTheStuff()
    {
        var plant = new PlanningDefinition { Research = ["Devilstrand"] ,Harvest=new PlanningHarvest {HarvestedThingDef = Devilstrand.Value}};
        var other = new PlanningDefinition { Harvest=new PlanningHarvest {HarvestedThingDef = "RawRice"}};
        // The real planning catalog also contains researched buildings with no harvested product.
        var building = new PlanningDefinition { Name = "Cooler", Research = ["Electricity"] };
        var map = GoalsReview.StuffResearch([plant, other, building]);
        Assert.Equal(["Devilstrand"], map[Devilstrand.Value]);
        Assert.False(map.ContainsKey("RawRice"));
        Assert.Single(map);
    }

    [Fact]
    public void TheStatusIsPursuingWithWearersAndNoMethodWithout()
    {
        var pursuing = GoalRules.Evaluate(Goal(), GoalFacts(Facts(Pawn("p1"))));
        Assert.Equal((GoalState.Pursuing, EconomyFamily.Resource, 2.0), (pursuing.State, pursuing.Owner, ((GoalDeficit.Measured)pursuing.Deficit).Amount));
        var none = GoalRules.Evaluate(Goal(), GoalFacts(Facts(Pawn("p1", canWear: false))));
        Assert.Equal((GoalState.NoMethod, "no_wearer"), (none.State, none.Reason));
        Assert.Equal("gear census", Assert.Throws<RequiredFactException>(() => GoalRules.Evaluate(Goal(), GoalFacts(new ReviewFacts()))).Fact);
    }
}
