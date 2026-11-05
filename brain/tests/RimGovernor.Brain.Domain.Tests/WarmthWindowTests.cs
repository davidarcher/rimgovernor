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
    private static readonly DashCandidate[] Stacks =
    [
        new() { Id = "near", PathCells = 400, Value = 10 },
        new() { Id = "far", PathCells = 400, Value = 9 },
    ];

    [Fact]
    public void EveryAvailablePawnGetsItsOwnWindowAndATripIsOfferedOnce()
    {
        var windows = DashPlanRules.PlanWarmth([], Stacks, Pawns(Colonist("b", 0), Colonist("a", 0)), -30, new Tick(100), Tuning);
        Assert.Equal(["a", "b"], windows.Select(w => w.Pawn.Value).ToList());
        var all = windows.SelectMany(w => w.Trips.Select(t => t.Id)).ToList();
        Assert.Equal(all.Distinct().Count(), all.Count);
        Assert.Equal(2, all.Count);
        Assert.All(windows, w => Assert.True(w.OpenAt(new Tick(100))));
    }

    [Fact]
    public void APawnStillWarmingUpGetsNoWindowUntilItsObservedSeverityRefills()
    {
        var cold = Pawns(Colonist("a", DashPlanRules.WarmthRefillSeverity + 0.01));
        Assert.Empty(DashPlanRules.PlanWarmth([], Stacks, cold, -30, new Tick(100), Tuning));
        var refilled = Pawns(Colonist("a", DashPlanRules.WarmthRefillSeverity));
        Assert.Single(DashPlanRules.PlanWarmth([], Stacks, refilled, -30, new Tick(100), Tuning));
        Assert.Equal(0, DashPlanRules.WarmthRefillTicks(DashPlanRules.WarmthRefillSeverity));
        Assert.True(DashPlanRules.WarmthRefillTicks(DashPlanRules.DashSeverityCap) > 0);
    }

    [Fact]
    public void AHeldWindowIsKeptBeforeItsBoundAndClosesAtItWithoutReopening()
    {
        var pawns = Pawns(Colonist("a", 0));
        var held = DashPlanRules.PlanWarmth([], Stacks, pawns, -30, new Tick(100), Tuning);
        var window = Assert.Single(held);
        var before = new Tick(window.Until.Value - 1);
        Assert.Equal(held, DashPlanRules.PlanWarmth(held, Stacks, pawns, -30, before, Tuning));
        Assert.Empty(DashPlanRules.PlanWarmth(held, Stacks, pawns, -30, window.Until, Tuning));
        Assert.Empty(DashPlanRules.PlanWarmth(held, Stacks, Pawns(), -30, before, Tuning));
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
