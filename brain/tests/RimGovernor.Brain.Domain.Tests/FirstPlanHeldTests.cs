using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The first layout plan waits for every station fact with the clock held (#3652): a fact the colony has not read
/// fails the derive naming it, which refuses the Run under FirstPlan, and full facts give the plan of the full graph.
/// </summary>
public sealed class FirstPlanHeldTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static ClockWatchDecision Decide(string firstPlanUnread) => ClockWatchRules.EvaluateClockWatch(
        new ClockWatchFacts
        {
            Current = World,
            Tick = new(100),
            SinceStarted = TimeSpan.FromSeconds(1),
            SinceObserved = TimeSpan.FromMilliseconds(500),
            Emergency = EmergencySnapshot.New(World, new(100), new EmergencyFacts
            {
                ColonistsComplete = Known(true),
                DependentPatients = Known(EquatableArray.From<EmergencyDependentPatient>([])),
            }),
            EventsOpen = true,
            Status = new ClockWatchStatus { Snapshot = World, Tick = new(100), State = ClockWatchState.Held, NativeTickBoundary = Known(true) },
            CombatPlan = Known(false),
            FirstPlanUnread = firstPlanUnread,
        },
        new ClockWatchLimits { MaxAge = TimeSpan.FromSeconds(10) });

    [Fact]
    public void AFirstPlanWithTheStockUnreadFailsNamingItAndHoldsTheClock()
    {
        var s = FirstLayoutPlanTests.Survey("mild");
        var f = FirstLayoutPlanTests.Facts(s, 3) with { Stock = Unknown<IReadOnlyDictionary<Resource, long>>() };

        var e = Assert.Throws<RequiredFactException>(() => LayoutDeriveRules.DeriveLayoutPlan(s, f, []));
        Assert.Equal("resource demand", e.Fact);

        var decision = Decide(e.Fact);
        Assert.False(decision.Run);
        Assert.Equal([ClockWatchReason.FirstPlan], decision.Refused.Items);
    }

    [Fact]
    public void AFirstPlanWithoutTheRoomsCensusFailsNamingTheDiningFurniture()
    {
        // The rooms census carries the dining furniture; a first plan read without it waits for it (#3340, #3652).
        var s = FirstLayoutPlanTests.Survey("mild");
        var f = FirstLayoutPlanTests.Facts(s, 3) with { Dining = new DiningFurniture() };

        var e = Assert.Throws<RequiredFactException>(() => LayoutStationPlanRules.AdviseStations(f));
        Assert.Equal("dining furniture", e.Fact);
    }

    [Fact]
    public void AFirstPlanWaitsForTheBedMergesPriceAndRecordsOnceItIsRead()
    {
        // The bed merge's heat and sleeper prices are required (#3747): unread, the derive fails naming them and the
        // clock stays held; once read, the first plan is derived.
        var s = FirstLayoutPlanTests.Survey("cold");
        var priced = FirstLayoutPlanTests.Priced(FirstLayoutPlanTests.Facts(s, 3), "cold");

        var mood = Assert.Throws<RequiredFactException>(() => LayoutDeriveRules.DeriveLayoutPlan(s, priced with { BedMerge = priced.BedMerge with { Sleepers = default } }, []));
        Assert.Equal("sleeper_mood", mood.Fact);
        var curve = Assert.Throws<RequiredFactException>(() => LayoutDeriveRules.DeriveLayoutPlan(s, priced with { OutdoorCurve = default }, []));
        Assert.Equal("outdoor_curve", curve.Fact);
        Assert.False(Decide(curve.Fact).Run);

        var (plan, _) = LayoutDeriveRules.DeriveLayoutPlan(s, priced, []);
        Assert.True(plan.IsKnown);
        Assert.NotEmpty(plan.Value.RoomsOf(LayoutSurveyRules.PlannedBedroom).Concat(plan.Value.RoomsOf(PlannedRole.Barracks)));
    }

    [Fact]
    public void AFirstPlanWithEveryFactReadIsDerivedFromTheFullGraph()
    {
        var s = FirstLayoutPlanTests.Survey("mild");
        var f = FirstLayoutPlanTests.Priced(FirstLayoutPlanTests.Facts(s, 3), "mild");
        var full = StationGraph.BuildStationGraph(f.GraphInput());

        var advice = LayoutStationPlanRules.AdviseStations(f);
        Assert.Equal(full.Rooms.Select(r => r.Id), advice.Graph.Rooms.Select(r => r.Id));
        Assert.Equal(full.Nodes.Count, advice.Graph.Nodes.Count);
        Assert.Contains(full.Nodes, n => n.Purpose == StationPurpose.Dine);

        var plan = FirstLayoutPlanTests.FirstPlan("mild", 3);
        // The seed rooms held no station: the full graph's beds give the plan its bedroom.
        Assert.NotEmpty(plan.RoomsOf(LayoutSurveyRules.PlannedBedroom));
    }

    [Fact]
    public void AHeldClocksFirstPlanRefusalIsARefusedRowNamingTheFactNotARunError()
    {
        // The refusal of a held clock throws its HeldException; the row is still the refusal's (layout/first-plan-held
        // found it filed as failed/run_error, with neither refused nor first_plan_fact).
        var note = new BuildingRuntime.ClockRunDriver.RunNote { Action = "refused", Decision = Decide("resource demand"), FirstPlanUnread = "resource demand" };
        var row = BuildingRuntime.ClockRunDriver.RunRow(new BuildingRuntime.ClockSchedulerResult(), note, new Executor.HeldException("clock run refused: FirstPlan"), TimeSpan.Zero);

        Assert.NotNull(row);
        Assert.Equal("refused", row.Verdict);
        Assert.Equal("first_plan", row.Reason);
        Assert.Equal(["first_plan"], Assert.IsType<List<string>>(row.Attrs["refused"]));
        Assert.Equal("resource demand", row.Attrs[Telemetry.TelemetryKinds.ClockRunFirstPlanFactKey]);
        Assert.False(row.Attrs.ContainsKey("error"));
    }

    [Fact]
    public void AnEmptyFirstPlanFactHoldsNothing()
    {
        var decision = Decide("");
        Assert.True(decision.Run, string.Join(",", decision.Refused.Items));
    }
}
