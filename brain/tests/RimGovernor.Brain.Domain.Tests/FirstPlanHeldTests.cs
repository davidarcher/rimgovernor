using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
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
            Emergency = EmergencySnapshot.New(World, new(100), new EmergencyFacts()),
            EventsOpen = true,
            Status = new ClockWatchStatus { Snapshot = World, Tick = new(100), State = ClockWatchState.Held, NativeTickBoundary = true },
            CombatPlan = false,
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

        var mood = Assert.Throws<RequiredFactException>(() => LayoutDeriveRules.DeriveLayoutPlan(s, priced with { BedMerge = new BedMergeFacts.Unread("sleeper_mood") }, []));
        Assert.Equal("sleeper_mood", mood.Fact);
        var curve = Assert.Throws<RequiredFactException>(() => LayoutDeriveRules.DeriveLayoutPlan(s, priced with { OutdoorCurve = default }, []));
        Assert.Equal("outdoor_curve", curve.Fact);
        Assert.False(Decide(curve.Fact).Run);

        var (plan, _) = LayoutDeriveRules.DeriveLayoutPlan(s, priced, []);
        Assert.NotNull(plan);
        Assert.NotEmpty(plan!.RoomsOf(LayoutSurveyRules.PlannedBedroom).Concat(plan!.RoomsOf(PlannedRole.Barracks)));
    }

    // The game's SleptInBedroom and SleptInBarracks stage offsets (Thoughts_Memory_RoomStats.xml) up to the stages the
    // catalog reads; null is SleptInBedroom's dull stage, where Toils_LayDown.ApplyBedThoughts gains no memory.
    private static readonly double?[] BedroomOffsets = [-2, null, 1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly string[] FreshIds = ["Thing_Human1", "Thing_Human2", "Thing_Human3"];
    private static readonly double?[] BarracksOffsets = [-7, -5, -4, -3, -2, -1, 1, 2, 3, 4];

    /// <summary>
    /// A fresh colonist's bed thought stages as NativeNeedsForecast.ThoughtStagesOf reads them: no owned bed, the mood and
    /// break threshold read, and each null stage a row at 0 (#3822), or left out as the read did before it.
    /// </summary>
    private static NeedsForecastPawn FreshColonist(string id, bool nullStageRow)
    {
        var stages = new List<NeedThoughtStage>();
        void Rows(string def, string role, double?[] offsets)
        {
            for (var s = 0; s < BedMergePriceTests.Levels.StageCount; s++)
            {
                if (offsets[s] is not null || nullStageRow)
                {
                    stages.Add(new(def, role, s, offsets[s] ?? 0, NeedsFixtures.Lasting((double)GameTime.TicksPerDay, false)));
                }
            }
        }
        Rows("SleptInBedroom", "slept_in_bedroom", BedroomOffsets);
        Rows("SleptInBarracks", "slept_in_barracks", BarracksOffsets);
        return NeedsFixtures.Pawn() with { Pawn = id, Mood = NeedsFixtures.Mood with { UnclampedTarget = 0.6, BreakThreshold = 0.35 }, ThoughtStages = EquatableArray.From(stages), Sleep = SleepFixtures.Ground() };
    }

    private static EquatableArray<SleeperGains>? FreshSleepers(bool nullStageRow) => LayoutClusterRules.SleeperGainsOf(new BedroomGainFacts
    {
        Pawns = FreshIds.ToDictionary(p => p, p => FreshColonist(p, nullStageRow), StringComparer.Ordinal),
        Breaks = new() { Threshold = 0.35, Tiers = [new(1, 2), new(1, 3), new(1, 5)] },
        WellbeingWeight = 0.1,
    }, BedMergePriceTests.Levels);

    [Fact]
    public void AFreshColonysFirstPlanReadsTheSleepersMoodAtTheBedroomsNullDullStage()
    {
        // #3822: SleptInBedroom has no dull stage, so the read left that row out, every sleeper's gain at it was unread
        // and the first plan of every colony failed on sleeper_mood at ChooseBeds, holding the clock for good.
        var s = FirstLayoutPlanTests.Survey("mild");
        var priced = FirstLayoutPlanTests.Priced(FirstLayoutPlanTests.Facts(s, 3), "mild");
        StationFacts With(EquatableArray<SleeperGains>? sleepers) => priced with
        {
            BedMerge = sleepers is { } gains ? ((BedMergeFacts.Read)priced.BedMerge) with { Sleepers = gains } : new BedMergeFacts.Unread("sleeper_mood"),
        };

        var before = Assert.Throws<RequiredFactException>(() => LayoutDeriveRules.DeriveLayoutPlan(s, With(FreshSleepers(nullStageRow: false)), []));
        Assert.Equal("sleeper_mood", before.Fact);
        Assert.StartsWith("LayoutBedMerge.cs:", before.Site, StringComparison.Ordinal);
        Assert.EndsWith(" ChooseBeds", before.Site, StringComparison.Ordinal);

        var read = FreshSleepers(nullStageRow: true);
        Assert.NotNull(read);
        var (plan, _) = LayoutDeriveRules.DeriveLayoutPlan(s, With(read), []);
        Assert.NotNull(plan);
        Assert.NotEmpty(plan!.RoomsOf(LayoutSurveyRules.PlannedBedroom).Concat(plan!.RoomsOf(PlannedRole.Barracks)));
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
