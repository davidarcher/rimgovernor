using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Store hauls as valued standby work (#3624).</summary>
public sealed class StoreHaulTests
{
    private const double ValuePerWork = 0.0036;
    private static readonly double Horizon = new ColonyModelPolicy().HorizonDays;
    private static readonly Cell Outside = new(10, 10), Store = new(20, 20), Site = new(30, 30);

    // A one-trip row: 300 ticks to reach the stack, 300 to carry it.
    private static ConstructionActivityOption Row(string pawn = "pawn", int count = 75) =>
        new("haul/stack", "", Store, pawn, "Steel", "", Outside, count, 75, "", Known(300d), Known(300d), default);

    private static StoreHaul Haul(string resource, double perDay = 0, double storedPerDay = 0, double fromOrigin = 0, double fromStore = 0,
        bool use = false, string thing = "stack") =>
        new(thing, resource, Outside, Store, 75, 1.9, 100, perDay, storedPerDay)
        {
            Options = [Row()],
            Uses = use ? [new(new(resource, Site, 75), Known(fromOrigin), Known(fromStore))] : [],
        };

    [Fact]
    public void AStackWithNoUseThatDoesNotDeteriorateIsWorthNothingToHaul()
    {
        // Steel, wood, blocks and plasteel earn nothing for deterioration (native reads their rate as 0): unused, hauling is only labor.
        var steel = ForecastValue.StoreHaulValueOf(Haul("Steel"), Row(), ValuePerWork, Horizon);
        Assert.Equal(0, steel.TravelSaved);
        Assert.Equal(0, steel.LossAvoided);
        Assert.True(steel.Total < 0);
        Assert.Empty(StoreHaulRules.Propose(Read(Haul("Steel")), ["pawn"], new HashSet<string>(), new HashSet<Cell>(), Horizon));
    }

    [Fact]
    public void AStackWithAForecastUseOrThatDeterioratesIsWorthHauling()
    {
        // Cloth outside loses 2 hp a day (rain and the unroofed factor), nothing in storage.
        var cloth = ForecastValue.StoreHaulValueOf(Haul("Cloth", perDay: 2), Row(), ValuePerWork, Horizon);
        Assert.True(cloth.LossAvoided > 0);
        Assert.True(cloth.Total > 0);
        // Steel a planned site will use, lying 900 ticks from the site where storage is 300.
        var steel = ForecastValue.StoreHaulValueOf(Haul("Steel", fromOrigin: 900, fromStore: 300, use: true), Row(), ValuePerWork, Horizon);
        Assert.Equal(0, steel.LossAvoided);
        Assert.True(steel.Total > 0);
        // Rot inside the horizon loses the whole stack where it lies.
        var meat = Haul("Meat") with { RotTicks = Known(2d * GameTime.TicksPerDay), StoredRotTicks = Known(1e9) };
        Assert.True(ForecastValue.StoreHaulValueOf(meat, Row(), ValuePerWork, Horizon).Total > 0);
    }

    [Theory]
    [InlineData(600, 300)]
    [InlineData(1200, 300)]
    [InlineData(2400, 300)]
    public void TheValueScalesWithTheTravelSaved(double fromOrigin, double fromStore)
    {
        var value = ForecastValue.StoreHaulValueOf(Haul("Steel", fromOrigin: fromOrigin, fromStore: fromStore, use: true), Row(), ValuePerWork, Horizon);
        // One trip there and back, each walking from storage instead of where the stack lies.
        Assert.Equal(2 * (fromOrigin - fromStore) / GameTime.TicksPerDay, value.TravelSaved, 9);
        var farther = ForecastValue.StoreHaulValueOf(Haul("Steel", fromOrigin: fromOrigin * 2, fromStore: fromStore, use: true), Row(), ValuePerWork, Horizon);
        Assert.True(farther.Total > value.Total);
        // Storage farther from the work than the stack costs travel.
        Assert.True(ForecastValue.StoreHaulValueOf(Haul("Steel", fromOrigin: fromStore, fromStore: fromOrigin, use: true), Row(), ValuePerWork, Horizon).Total < 0);
    }

    [Fact]
    public void AStandbyHaulNeverTakesAStackAPendingServiceUses()
    {
        var cloth = Haul("Cloth", perDay: 2);
        var meal = Haul("MealSimple", perDay: 2, thing: "meal");
        var read = Read(cloth, meal);
        Assert.Equal("meal", Assert.Single(StoreHaulRules.Propose(read, ["pawn"], new HashSet<string>(["stack"]), new HashSet<Cell>(), Horizon)).Haul.Thing);
        Assert.Empty(StoreHaulRules.Propose(read, ["pawn"], new HashSet<string>(), new HashSet<Cell>([Outside]), Horizon));
        // Each pawn takes its best haul, and a stack goes once.
        Assert.Single(StoreHaulRules.Propose(read, ["pawn"], new HashSet<string>(), new HashSet<Cell>(), Horizon));
    }

    [Fact]
    public void ADeferredColonistIsAdmittedToStandbyAndIsNotFreedAgain()
    {
        // The review deferred "pawn" (no change due): standby admits it instead of a bounded idle that finished every 600 ticks.
        var facts = Facts(Native("pawn"));
        Assert.Equal(["pawn"], StandbyAssignmentsRules.StandbyPawnsOf(facts, new([]), new HashSet<string>()));
        var standby = StandbyAssignmentsRules.StandbyActivity("pawn", 7, StandbyAssignmentsRules.StandbyMethodPrefix + "1");
        var held = standby.Orders.Where(o => !o.Release).Select(o => o.Pawn).ToHashSet(StringComparer.Ordinal);
        var assigned = ClockScheduler.HeldAfterReview(new HashSet<string>(StringComparer.Ordinal), held);
        // Standby has no deadline: the next steps read it held, so freed does not fire again.
        Assert.False(ClockScheduler.PawnsFreed(assigned, held));
        Assert.False(ClockScheduler.PawnsFreed(held, held));
        // On standby, a later review gives it no second standby, but standby work may still take it.
        facts = Facts(Native("pawn", standby.Orders[0].Assignment, 1));
        Assert.Empty(StandbyAssignmentsRules.StandbyPawnsOf(facts, new([]), new HashSet<string>()));
        Assert.Equal(["pawn"], StandbyAssignmentsRules.StandbyWorkPawnsOf(facts, new([]), new HashSet<string>()));
    }

    [Fact]
    public void AHaulBindsAsOneDeliverOfTheStackToItsStorageCell()
    {
        var proposal = Assert.Single(StoreHaulRules.Propose(Read(Haul("Cloth", perDay: 2)), ["pawn"], new HashSet<string>(), new HashSet<Cell>(), Horizon));
        var batch = StoreHaulRules.Activity(proposal, 7, StandbyAssignmentsRules.StoreHaulMethodPrefix + "1", 100, 5000);
        batch.Validate();
        var order = Assert.Single(batch.Orders);
        var step = Assert.Single(order.Steps);
        Assert.Equal(PawnActivityKind.Deliver, step.Kind);
        Assert.Equal("stack", step.Target);
        Assert.Equal(75, step.Count);
        Assert.Equal(Store, step.Destination);
        Assert.StartsWith(StandbyAssignmentsRules.StoreHaulMethodPrefix, order.Method);
    }

    private static StoreHaulRead Read(params StoreHaul[] hauls) => new(ValuePerWork) { Hauls = EquatableArray.From(hauls) };

    private static WorkResourcePawn Native(string id, string assignment = "", long revision = 0) =>
        new(id, assignment, revision, assignment.Length == 0 ? "" : "started", "", "", 0);

    private static RoundsFacts Facts(params WorkResourcePawn[] native)
    {
        var tick = new Tick(1000);
        var evidence = new WorkAdmissionEvidence
        {
            Tick = tick, Pawns = Known(EquatableArray.From(native.Select(n => new WorkPawnAvailability(n.Pawn, Known(true), Known(true), Known(false), default)))),
            WorkResources = Known(EquatableArray.From(native)),
        };
        return new RoundsFacts { Tick = tick, WorkView = WorkRules.WorkResourcesOf(evidence, default, []) };
    }
}
