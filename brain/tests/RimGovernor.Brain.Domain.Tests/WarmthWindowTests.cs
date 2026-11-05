using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class WarmthWindowTests
{
    private static readonly HypothermiaTuning Tuning = DashEstimateRules.PublishedHypothermiaTuning;

    private static WorkPawn Colonist(string id, double severity) => PawnFixtures.Worker(id) with
    {
        Observed = PawnFixtures.Row(id) with
        {
            Settings = PawnFixtures.Row(id).Settings with { Body = PawnFixtures.Body with { ComfortableMinC = 0, Hypothermia = severity } },
        },
    };

    private static WorkPawnsRead Pawns(params WorkPawn[] rows) => new(EquatableArray.From(rows));

    // Each stack is a long walk: one fits a budget at -30 °C, two do not, so a second pawn is needed for the second.
    private static readonly WarmthCandidate[] Stacks =
    [
        new() { Id = "near", PathCells = 400, Value = 10 },
        new() { Id = "far", PathCells = 400, Value = 9 },
    ];

    [Fact]
    public void EveryAvailablePawnGetsItsOwnWindowAndATripIsOfferedOnce()
    {
        var windows = WarmthPlanRules.PlanWarmth([], Stacks, Pawns(Colonist("b", 0), Colonist("a", 0)), -30, new Tick(100), Tuning);
        Assert.Equal(["a", "b"], windows.Select(w => w.Pawn.Value).ToList());
        var all = windows.SelectMany(w => w.Trips.Select(t => t.Id)).ToList();
        Assert.Equal(all.Distinct().Count(), all.Count);
        Assert.Equal(2, all.Count);
        Assert.All(windows, w => Assert.True(w.OpenAt(new Tick(100))));
    }

    [Fact]
    public void APawnStillWarmingUpGetsNoWindowUntilItsObservedSeverityRefills()
    {
        var cold = Pawns(Colonist("a", WarmthPlanRules.WarmthRefillSeverity + 0.01));
        Assert.Empty(WarmthPlanRules.PlanWarmth([], Stacks, cold, -30, new Tick(100), Tuning));
        var refilled = Pawns(Colonist("a", WarmthPlanRules.WarmthRefillSeverity));
        Assert.Single(WarmthPlanRules.PlanWarmth([], Stacks, refilled, -30, new Tick(100), Tuning));
        Assert.Equal(0, WarmthPlanRules.WarmthRefillTicks(WarmthPlanRules.WarmthRefillSeverity));
        Assert.True(WarmthPlanRules.WarmthRefillTicks(WarmthPlanRules.WarmthSeverityCap) > 0);
    }

    [Fact]
    public void AHeldWindowIsKeptBeforeItsBoundAndClosesAtItWithoutReopening()
    {
        var pawns = Pawns(Colonist("a", 0));
        var held = WarmthPlanRules.PlanWarmth([], Stacks, pawns, -30, new Tick(100), Tuning);
        var window = Assert.Single(held);
        var before = new Tick(window.Until.Value - 1);
        Assert.Equal(held, WarmthPlanRules.PlanWarmth(held, Stacks, pawns, -30, before, Tuning));
        Assert.Empty(WarmthPlanRules.PlanWarmth(held, Stacks, pawns, -30, window.Until, Tuning));
        Assert.Empty(WarmthPlanRules.PlanWarmth(held, Stacks, Pawns(), -30, before, Tuning));
    }

    private static WarmthRequest Request(params WarmthCandidate[] candidates) => new()
    {
        Now = new Tick(100),
        Temperature = -30,
        ComfortMin = 0,
        Severity = 0,
        Speed = 4.6,
        Tuning = Tuning,
        Candidates = EquatableArray.From(candidates),
    };

    private static readonly Resource Wood = new("WoodLog");
    private static readonly Resource Steel = new("Steel");

    [Fact]
    public void AFarFuelStackOutranksANearerRicherStackWhileTheRunwayIsShort()
    {
        var needs = new WarmthNeeds { Fuel = new HashSet<Resource> { Wood } };
        var fuel = new WarmthCandidate { Id = "wood", Kind = WarmthTripKind.Loot, Tier = WarmthPlanRules.WarmthTierOf(Wood, false, needs), PathCells = 300, Value = 20 };
        var steel = new WarmthCandidate { Id = "steel", Kind = WarmthTripKind.Loot, Tier = WarmthPlanRules.WarmthTierOf(Steel, false, needs), PathCells = 20, Value = 400 };
        Assert.Equal(WarmthTier.Fuel, fuel.Tier);
        Assert.Equal(WarmthTier.Other, steel.Tier);
        // Alone, the budget holds either; together it holds the fuel trip and then whatever is left.
        var plan = WarmthPlanRules.PlanTrips(Request(steel, fuel));
        Assert.Equal("wood", plan.Trips[0].Id);
        Assert.Equal(WarmthTripKind.Loot, plan.Trips[0].Kind);
        var without = WarmthPlanRules.PlanTrips(Request(steel, fuel with { Tier = WarmthTier.Other }));
        Assert.Equal("steel", without.Trips[0].Id);
    }

    [Fact]
    public void AHuntWhoseEngagementCrossesTheCapIsLeftOutWhileANearerForageFits()
    {
        var hunt = new WarmthCandidate { Id = "deer", Kind = WarmthTripKind.LoneHunt, Tier = WarmthTier.Food, PathCells = 400, Value = 100, WorkTicks = 20 * GameTime.TicksPerHour };
        var forage = new WarmthCandidate { Id = "berries", Kind = WarmthTripKind.Forage, Tier = WarmthTier.Food, PathCells = 40, Value = 10, WorkTicks = WarmthPlanRules.WarmthForageWorkTicks };
        var plan = WarmthPlanRules.PlanTrips(Request(hunt, forage));
        var trip = Assert.Single(plan.Trips);
        Assert.Equal("berries", trip.Id);
        Assert.Equal(WarmthTripKind.Forage, trip.Kind);
        Assert.True(trip.Exposure > WarmthPlanRules.WarmthForageWorkTicks);
    }

    [Fact]
    public void WithinATierTripsRankByValuePerExposedTick()
    {
        var rich = new WarmthCandidate { Id = "rich", Tier = WarmthTier.Other, PathCells = 100, Value = 100 };
        var dense = new WarmthCandidate { Id = "dense", Tier = WarmthTier.Other, PathCells = 10, Value = 50 };
        var plan = WarmthPlanRules.PlanTrips(Request(rich, dense));
        Assert.Equal(["dense", "rich"], plan.Trips.Select(t => t.Id).ToList());
        Assert.Equal(WarmthTier.ShelterMaterials, WarmthPlanRules.WarmthTierOf(Wood, true, new WarmthNeeds { ShelterMaterials = new HashSet<Resource> { Wood }, Fuel = new HashSet<Resource> { Wood }, Food = true }));
        Assert.Equal(WarmthTier.Food, WarmthPlanRules.WarmthTierOf(Wood, true, new WarmthNeeds { Food = true }));
        Assert.Equal(WarmthTier.Other, WarmthPlanRules.WarmthTierOf(Wood, true, new WarmthNeeds()));
    }

    private static AcquisitionSource Source(string id, string resource, double yield, RimGovernor.Brain.GameFacts.Observations.AcquisitionKind kind, int x) => new()
    {
        Observed = new() { Source = new() { Id = id }, Token = id, Resource = resource, Yield = yield, Designation = new RimGovernor.Brain.GameFacts.Observations.AcquisitionDesignation.Open(), Taken = false, Kind = kind },
        Entity = new() { Id = id, DefName = id, Label = id, Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(1, new Cell(x, 0)) },
        NutritionYield = 1,
    };

    [Fact]
    public void TheCensusesOfferLoneHuntsAndForagesFromTheHomeReferenceCell()
    {
        var prey = Source("deer", "Meat_Deer", 1, new RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Hunt(80, 1, false, false, false, false), 30);
        var bear = Source("bear", "Meat_Bear", 1, new RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Hunt(200, 1, false, false, false, false), 10) with { Predator = true };
        var berries = Source("berries", "RawBerries", 8, new RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Plant(true, 1, false), 20);
        var tree = Source("oak", "WoodLog", 30, new RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Plant(false, 1, false), 5) with { Tree = true };
        var f = new ReviewFacts
        {
            Acquisition = [prey, bear, berries, tree],
            Items = new ItemFacts { Market = new Dictionary<Resource, double> { [new Resource("Meat_Deer")] = 2, [new Resource("Meat_Bear")] = 2, [new Resource("RawBerries")] = 1, [new Resource("WoodLog")] = 1 } },
            HomeCoverage = new() { Revision = 1, AutoHomeArea = false, HomeCells = [new Cell(0, 0)], Targets = [] },
        };
        var needs = new WarmthNeeds { Food = true };
        var rows = WarmthPlanRules.WarmthCandidates(f, needs);
        Assert.Equal(["berries", "deer"], rows.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToList());
        var hunt = rows.Single(r => r.Kind == WarmthTripKind.LoneHunt);
        Assert.Equal(160, hunt.Value);
        Assert.Equal(30, hunt.PathCells);
        Assert.Equal(WarmthPlanRules.WarmthHuntEngagementTicks, hunt.WorkTicks);
        Assert.Equal(WarmthTier.Food, hunt.Tier);
        var forage = rows.Single(r => r.Kind == WarmthTripKind.Forage);
        Assert.Equal(8, forage.Value);
        Assert.Equal(WarmthPlanRules.WarmthForageWorkTicks, forage.WorkTicks);
        Assert.Empty(WarmthPlanRules.WarmthCandidates(f with { HomeCoverage = null }, needs));
        var window = new WarmthWindow { Pawn = new PawnId("a"), Until = new Tick(10), Trips = [new WarmthTrip { Id = "deer", Kind = WarmthTripKind.LoneHunt }] };
        Assert.True(window.Names(WarmthTripKind.LoneHunt, "deer"));
        Assert.False(window.Names(WarmthTripKind.Forage, "deer"));
    }

    [Fact]
    public void TheFactsReadTheWindowOfOnePawn()
    {
        var f = new ReviewFacts { Tick = new Tick(5), Warmth = [new WarmthWindow { Pawn = new PawnId("a"), Until = new Tick(10) }] };
        Assert.True(f.WarmthOpenFor(new PawnId("a")));
        Assert.False(f.WarmthOpenFor(new PawnId("b")));
        Assert.True(f.WarmthOpen());
        Assert.False((f with { Tick = new Tick(10) }).WarmthOpen());
    }
}
