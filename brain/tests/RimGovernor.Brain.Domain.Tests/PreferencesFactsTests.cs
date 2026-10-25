using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The owning policies read the player's preferences from the facts and never propose what they forbid (#3495): diet,
/// human butchery, human leather, protected things and protected pawns. A default record reproduces the behavior
/// without preferences. Organ harvest and slavery are beside the surgery planner's tests (SurgerySelectionTests).
/// </summary>
public sealed class PreferencesFactsTests
{
    private static readonly PreferencesRecord None = new();

    private static PreferencesRecord Prefer(
        IReadOnlyList<DietOverride>? diet = null,
        IReadOnlyList<ForbiddenMethod>? forbidden = null,
        IReadOnlyList<string>? things = null,
        IReadOnlyList<string>? pawns = null) =>
        new() { Diet = diet ?? [], Forbidden = forbidden ?? [], ProtectedThings = things ?? [], ProtectedPawns = pawns ?? [] };

    private static readonly IReadOnlyList<Food> Foods =
    [
        new("Meat_Human", FoodKind.HumanMeat, MealIngredients.None),
        new("Meat_Megaspider", FoodKind.InsectMeat, MealIngredients.None),
        new("Meat_Muffalo", FoodKind.RawMeat, MealIngredients.None),
        new("RawPotatoes", FoodKind.Vegetable, MealIngredients.None),
    ];

    private static readonly PawnTrait CannibalTrait = new() { Observed = new() { DefName = "Cannibal" }, Effects = new TraitEffects { Cannibal = true } };

    private static IReadOnlyList<string> Allowed(IReadOnlyList<PawnTrait> traits, PreferencesRecord preferences)
    {
        var (diet, ok) = FoodPolicyRules.DietOf(traits, [], new IdeologyMode.NotInstalled(), preferences);
        Assert.True(ok);
        return FoodPolicyRules.DietFoods(diet, Foods);
    }

    [Fact]
    public void DefaultPreferencesLeaveTheTraitDiet()
    {
        Assert.Equal(["Meat_Muffalo", "RawPotatoes"], Allowed([], None));
        Assert.Equal(["Meat_Human", "Meat_Muffalo", "RawPotatoes"], Allowed([CannibalTrait], None));
    }

    [Fact]
    public void ACannibalDietAllowsHumanMeatForEveryColonist()
    {
        Assert.Equal(["Meat_Human", "Meat_Muffalo", "RawPotatoes"], Allowed([], Prefer(diet: [DietOverride.Cannibal])));
    }

    [Fact]
    public void AnInsectMeatDietAllowsInsectMeat()
    {
        Assert.Equal(["Meat_Megaspider", "Meat_Muffalo", "RawPotatoes"], Allowed([], Prefer(diet: [DietOverride.InsectMeat])));
    }

    [Fact]
    public void AVegetarianDietRemovesMeat()
    {
        Assert.Equal(["RawPotatoes"], Allowed([CannibalTrait], Prefer(diet: [DietOverride.Vegetarian])));
    }

    [Fact]
    public void ForbiddenHumanButcheryAndInsectMeatRemoveThemWhateverTheTraitsAndTheDiet()
    {
        var forbidden = Prefer(diet: [DietOverride.Cannibal, DietOverride.InsectMeat], forbidden: [ForbiddenMethod.HumanButchery, ForbiddenMethod.InsectMeat]);
        Assert.Equal(["Meat_Muffalo", "RawPotatoes"], Allowed([CannibalTrait], forbidden));
    }

    private static ProductionBench Bench() => ProductionBenchFixtures.Butchering("bench1", "ButcherSpot", "bench1-token", true) with
    {
        HumanCorpseNutrition = 60.0,
        Recipes = [new ProductionRecipe { Access = ProductionBenchFixtures.Recipe("ButcherCorpseFlesh"), Role = RecipeRole.ButcherFlesh, Food = new RecipeFood.NotFood() }],
        HumanButchers = [new HumanButcherCandidate { Observed = new() { PawnId = "worker1", CanWork = true, PawnTraits = [] }, Traits = EquatableArray.From<PawnTrait>([]), Ideology = new RimGovernor.Brain.GameFacts.Observations.PawnIdeo.None() }],
    };

    [Fact]
    public void ForbiddenHumanButcheryProposesNoButcherBill()
    {
        var benches = EquatableArray.From([Bench()]);
        var ideology = new IdeologyMode.NotInstalled();

        var (selection, ok) = HumanFoodRules.SelectHumanButcher(benches, ideology, None);
        Assert.True(ok);
        Assert.Equal(("bench1", "worker1"), (selection.Bench, selection.Worker));

        Assert.False(HumanFoodRules.SelectHumanButcher(benches, ideology, Prefer(forbidden: [ForbiddenMethod.HumanButchery])).Ok);
    }

    [Fact]
    public void AProtectedBuildingIsInNoGroundClearanceStep()
    {
        var rows = new[]
        {
            new ClearanceTarget { EntityId = "Table1", Player = true },
            new ClearanceTarget { EntityId = "Table2", Player = true },
            new ClearanceTarget { EntityId = "Rubble1" },
        };

        var (others, player) = ClearanceGroundRules.SplitGroundRows(rows, None);
        Assert.Equal(["Rubble1"], others.Select(r => r.EntityId));
        Assert.Equal(["Table1", "Table2"], player.Select(r => r.EntityId));

        (others, player) = ClearanceGroundRules.SplitGroundRows(rows, Prefer(things: ["Table1"]));
        Assert.Equal(["Table2"], player.Select(r => r.EntityId));
        Assert.Equal(["Rubble1"], others.Select(r => r.EntityId));
    }

    private static SquadDefenderFacts Fighter(string id) => new()
    {
        Id = new PawnId(id),
        Dead = false,
        Downed = false,
        Drafted = false,
        ViolenceCapable = true,
        NeedsTend = false,
        HealthFraction = 1.0,
        Armor = new DefenderArmor.Unmirrored(),
    };

    [Fact]
    public void AProtectedPawnIsHeldBackFromTheDraftWhileAnotherFighterCanFight()
    {
        var fighters = new[] { Fighter("a"), Fighter("b") };

        Assert.Empty(PreferenceRules.HeldBackFromDraft(None, fighters));
        Assert.Equal([new PawnId("a")], PreferenceRules.HeldBackFromDraft(Prefer(pawns: ["a"]), fighters));
    }

    [Fact]
    public void AProtectedPawnIsDraftedWhenNobodyElseCanFight()
    {
        var downed = Fighter("b") with { Downed = true };

        Assert.Empty(PreferenceRules.HeldBackFromDraft(Prefer(pawns: ["a"]), [Fighter("a"), downed]));
        Assert.Empty(PreferenceRules.HeldBackFromDraft(Prefer(pawns: ["a", "b"]), [Fighter("a"), Fighter("b")]));
    }

    [Fact]
    public void ForbiddenHumanLeatherLeavesTheStuffCatalogAndTheApparelOptions()
    {
        var cloth = new Resource("Cloth");
        var option = new GearOption { Origin = new GearOptionOrigin.Observed(GearFixtures.Option("o1", "Apparel_Parka", (PreferenceRules.HumanLeather).Value, GearSource.Worn) with { Condition = 0, Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Awful) }) };
        var plain = new GearOption { Origin = new GearOptionOrigin.Observed(GearFixtures.Option("o2", "Apparel_Parka", (cloth).Value, GearSource.Worn) with { Condition = 0, Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Awful) }) };
        var facts = new ReviewFacts
        {
            Items = new ItemFacts
            {
                StuffCategories = new Dictionary<Resource, IReadOnlyList<string>> { [cloth] = ["Fabric"], [PreferenceRules.HumanLeather] = ["Leathery"] },
                StuffBeauty = new Dictionary<Resource, double> { [cloth] = 1.0, [PreferenceRules.HumanLeather] = 1.0 },
            },
            Gear = ColonyModelFixtures.NoGear() with
            {
                Pawns = [new GearPawn { Observed = GearFixtures.Loadout("") with { Token = "", Pawn = new PawnId("p") }, LoadoutModel = new GearModel.Modeled(new GearLoadoutInput { Observed = GearFixtures.Loadout(""), Body = EmergencyFixtures.Placed("").Settings.Body with { ComfortableMinC = 0, ComfortableMaxC = 0 }, Threat = ColonyModelFixtures.NoThreat, Options = EquatableArray.From([option, plain]), Role = GearFixtures.Role }) }],
            },
        };

        var untouched = facts.WithPreferences(None);
        Assert.Equal(2, untouched.Items.StuffCategories.Count);

        var stamped = facts.WithPreferences(Prefer(forbidden: [ForbiddenMethod.HumanLeather]));
        Assert.Equal([cloth], stamped.Items.StuffCategories.Keys);
        Assert.Equal([cloth], stamped.Items.StuffBeauty.Keys);
        var gear = stamped.Gear;
        var input = Assert.IsType<GearModel.Modeled>(gear.Pawns[0].LoadoutModel).Input;
        Assert.Equal(["o2"], input.Options.Select(o => o.ID));
    }

    [Fact]
    public void TheStampReachesThePrisonerDecisions()
    {
        var facts = new ReviewFacts { Population = PopulationFixtures.Census(colony: new PrisonerColony { Colonists = 2 }) }.WithPreferences(Prefer(forbidden: [ForbiddenMethod.Slavery]));

        var colony = facts.Population.Colony;
        Assert.True(colony.Preferences.Forbids(ForbiddenMethod.Slavery));
        Assert.True(facts.Preferences.Forbids(ForbiddenMethod.Slavery));
    }
}
