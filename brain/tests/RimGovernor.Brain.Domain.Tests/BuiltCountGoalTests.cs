using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Built-count goals (#3496 part D): a turret goal raises the defense layout's turret budget, a trade beacon and a
/// geothermal generator are placed by the goals family itself, and a definition nothing places reports it.
/// </summary>
public sealed class BuiltCountGoalTests
{
    private static GoalRecord Built(int id, string thing, int count) => new() { Id = id, Kind = GoalKind.BuiltCount, Target = thing, Count = count };

    private static RoundsFacts Standing(params (string Thing, long Count)[] built) => new()
    {
        BuiltBuildings = EquatableArray.From(built.Select(b => new Amount(new Resource(b.Thing), b.Count))),
    };

    private static GoalsFacts Facts(GoalRecord goal, RoundsFacts rounds) => new() { Goals = [goal], Rounds = rounds };

    [Fact]
    public void ATurretGoalRaisesTheTurretBudgetByMaxAndRidesEveryRead()
    {
        var goals = new GoalsFacts { Goals = [Built(1, DefenseTurretsPolicy.TurretMini, 4), Built(2, DefenseTurretsPolicy.TurretAutocannon, 6), Built(3, DefenseTurretsPolicy.TurretMini, 2)] };
        var demand = GoalRules.DemandOf(goals);
        Assert.Equal(6, demand.Build.Turrets);
        Assert.Equal(6, GoalRules.Stamp(new RoundsFacts(), demand).GoalBuild.Turrets);

        var none = Known<EquatableArray<DefenseTurretFacts>>([]);
        // With no raid shortfall the budget is the floor; a larger budget from the raid stands.
        Assert.Equal(6, DefenseBudget.TurretBudget(new RaidDefense(0.0, 0.0), none, 6));
        Assert.Equal(0, DefenseBudget.TurretBudget(new RaidDefense(0.0, 0.0), none, 0));
        Assert.Equal(1, DefenseBudget.TurretBudget(new RaidDefense(500.0, 0.0), none, 0));
        Assert.Equal(6, DefenseBudget.TurretBudget(new RaidDefense(500.0, 0.0), none, 6));
        Assert.Equal(3, DefenseIEDs.IEDBudget(new RaidDefense(0, 0), none, 3));
    }

    [Fact]
    public void ATurretGoalIsPursuedByTheDefenseLayoutAndMetOnceTheyStand()
    {
        var goal = Built(1, DefenseTurretsPolicy.TurretMini, 3);
        var open = Facts(goal, Standing((DefenseTurretsPolicy.TurretMini, 1))) with { Owners = new Dictionary<NeedId, OwnerProgress> { [LayoutFamily.Defense] = new(true, "") } };
        var status = GoalRules.Evaluate(goal, open);
        Assert.Equal((GoalState.Pursuing, LayoutFamily.Defense, 2.0), (status.State, status.Owner, ((GoalDeficit.Measured)status.Deficit).Amount));

        var blocked = open with { Owners = new Dictionary<NeedId, OwnerProgress> { [LayoutFamily.Defense] = new(false, "turret_power") } };
        Assert.Equal((GoalState.Blocked, "turret_power"), (GoalRules.Evaluate(goal, blocked).State, GoalRules.Evaluate(goal, blocked).Reason));

        Assert.Equal(GoalState.Met, GoalRules.Evaluate(goal, Facts(goal, Standing((DefenseTurretsPolicy.TurretMini, 3)))).State);
        // A met goal keeps its floor, or the budget would drop below the turrets standing.
        Assert.Equal(3, GoalRules.DemandOf(Facts(goal, Standing((DefenseTurretsPolicy.TurretMini, 3)))).Build.Turrets);
    }

    [Fact]
    public void ADefinitionNothingPlacesReportsNoMethodNamingIt()
    {
        var goal = Built(1, "Turret_Mortar", 2);
        var status = GoalRules.Evaluate(goal, Facts(goal, Standing(("Turret_Mortar", 1))));
        Assert.Equal((GoalState.NoMethod, "no placer for Turret_Mortar", 1.0), (status.State, status.Reason, ((GoalDeficit.Measured)status.Deficit).Amount));
        Assert.Equal(default, status.Owner);
        Assert.Equal(0, GoalRules.DemandOf(Facts(goal, new RoundsFacts())).Build.Turrets);

        // A definition already standing is still Met.
        Assert.Equal(GoalState.NoMethod, GoalRules.Evaluate(goal, Facts(goal, new RoundsFacts())).State);
        Assert.Equal(GoalState.Met, GoalRules.Evaluate(goal, Facts(goal, Standing(("Turret_Mortar", 2)))).State);

        var held = new GoalsFamily().Propose([new Need { Id = GoalRules.NeedOf(goal), Tier = NeedTier.Development, Satisfaction = Satisfaction.Unmet }], Facts(goal, new RoundsFacts()));
        Assert.Equal("no_method:no placer for Turret_Mortar", Assert.Single(held).Hold!.Subject);
    }

    [Theory]
    [InlineData(GoalRules.TradeBeaconDefinition, BuiltPlacer.Beacon)]
    [InlineData(PowerRules.GeothermalDefinition, BuiltPlacer.Geothermal)]
    public void TheBeaconAndTheGeothermalGeneratorAreThePlacedByTheGoalsOwnNeed(string thing, BuiltPlacer placer)
    {
        Assert.Equal(placer, GoalRules.PlacerOf(thing));
        var goal = Built(1, thing, 2);
        Assert.True(GoalRules.OwnStep(goal));
        var own = GoalRules.NeedOf(goal);

        var open = Facts(goal, Standing((thing, 1)));
        var status = GoalRules.Evaluate(goal, open);
        Assert.Equal((GoalState.Pursuing, own, 1.0), (status.State, status.Owner, ((GoalDeficit.Measured)status.Deficit).Amount));
        Assert.Equal(GoalState.Blocked, GoalRules.Evaluate(goal, open with { Owners = new Dictionary<NeedId, OwnerProgress> { [own] = new(false, "no_space") } }).State);
        Assert.Equal(GoalState.Met, GoalRules.Evaluate(goal, Facts(goal, Standing((thing, 2)))).State);
        Assert.Equal(0, GoalRules.DemandOf(open).Build.Turrets);

        // The goal's work step reports its own verdict, so the family holds no Method for it.
        Assert.Empty(new GoalsFamily().Propose([new Need { Id = own, Tier = NeedTier.Development, Satisfaction = Satisfaction.Unmet }], open));
    }

    [Fact]
    public void ADefinitionNoPlannerOwnsIsNotOwnedByTheGoalsFamily()
    {
        Assert.Equal(BuiltPlacer.None, GoalRules.PlacerOf("Bed"));
        Assert.False(GoalRules.OwnStep(Built(1, "Bed", 1)));
        Assert.False(GoalRules.OwnStep(Built(1, DefenseTurretsPolicy.TurretMini, 1)));
        Assert.All(DefenseTurretsPolicy.TurretLadder, t => Assert.Equal(BuiltPlacer.Turret, GoalRules.PlacerOf(t)));
    }

    [Fact]
    public void TheGeothermalStepOffersTheNearestFreeGeyserFirst()
    {
        PowerGeyser Geyser(string id, int x, int z, bool occupied = false) => new() { Id = id, Cell = new Cell(x + 1, z + 1), Occupied = occupied, Cells = [new Cell(x, z), new Cell(x + 1, z), new Cell(x, z + 1), new Cell(x + 1, z + 1)] };
        var geysers = new[] { Geyser("far", 80, 80), Geyser("taken", 21, 20, occupied: true), Geyser("near", 30, 30) };
        var sites = GoalsRounds.GeyserSites(geysers, Known(new Cell(20, 20)), new Bounds(2, 2)).ToList();
        Assert.Equal([new Rectangle(30, 30, 2, 2), new Rectangle(80, 80, 2, 2)], sites);
    }
}
