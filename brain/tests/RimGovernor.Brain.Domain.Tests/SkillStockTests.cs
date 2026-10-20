using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using SurgeryKind = RimGovernor.Brain.Policy.SurgeryKind;
using SurgeryOperation = RimGovernor.Brain.Policy.SurgeryOperation;

namespace RimGovernor.Brain.Domain.Tests;

// #3634: the tier-2 skill stock, the practice node that trains it, the medicine stock by kind, and their per-unit marginals.
public sealed class SkillStockTests
{
    private const double Day = GameTime.TicksPerDay;

    // The practice pawn's 14.1 free hours a day of surgery at 1.6 raw XP a tick (ProjectGraphTests): 56 400 raw XP a day, applied as
    // 4000 unsaturated plus a fifth of the rest.
    private const double AppliedPerDay = 4000 + ((14.1 * GameTime.TicksPerHour * 1.6) - 4000) * 0.2;

    private const double RequiredXp = 100_000;

    private const string Key = "skill/Anna/Medicine";

    // Twenty-one levels of 100 000 XP without decay: a run of practice crosses about nine of them.
    internal static SkillLearning Mechanics(int stored, double xp) => new()
    {
        Disabled = Known(false), TrackerAllowsLearning = Known(true), StoredLevel = Known(stored), Aptitude = Known(0), Xp = Known(xp),
        XpSinceMidnight = Known(0d), DirectFactor = Known(1d), UnsaturatedFactor = Known(1d), SaturationThreshold = Known(4000d),
        SaturatedFactor = Known(.2), LossThreshold = Known(-1000d), DecayInterval = Known(200), NextDecay = Known(200), ValidForTicks = Known(1000),
        Levels = EquatableArray.From(Enumerable.Range(0, 21).Select(l => new SkillLevelMechanics(l, RequiredXp, 0))),
        PracticeEligible = Known(true), PracticeCeiling = Known(2), RangeAppliedBudget = Known(30d),
    };

    /// <summary>The pawn with only a Medicine skill on the mechanics, and a surgery speed of <paramref name="speed"/> at every level.</summary>
    internal static ModelPawn Practitioner(ModelPawn pawn, SkillLearning mechanics, double speed = 1) => pawn with
    {
        Skills = Known(EquatableArray.From(new[] { new WorkSkill { Name = "Medicine", Learning = mechanics } })),
        SkillStatCurves = EquatableArray.From(new[]
        {
            new SkillStatCurve("MedicalOperationSpeed", "Medicine", EquatableArray.From(Enumerable.Range(0, 21).Select(l => new SkillStatLevel(l, speed))), "test"),
        }),
    };

    /// <summary>A peg leg practice slot on prisoner P1: 1500 work, 1.6 raw XP a tick, one medicine a surgery.</summary>
    internal static ModelPracticeSlot Slot(string speedStat = "") => new()
    {
        Prisoner = "P1", Recipe = "InstallPegLeg", Skill = "Medicine", Work = 1500, RawXpPerWorkTick = Known(1.6), SpeedStat = speedStat, Medicine = 1,
    };

    /// <summary>Anna alone, practising on the slot, with plenty of medicine.</summary>
    private static ColonyModelInput Practising(SkillLearning mechanics, double medicine = 100_000, string speedStat = "", double speed = 1)
    {
        var input = ProjectGraphTests.Colony();
        return input with
        {
            Pawns = Known(EquatableArray.From(new[] { Practitioner(ProjectGraphTests.Pawn("Anna", null, null), mechanics, speed) })),
            Practice = Known(EquatableArray.From(new[] { Slot(speedStat) })),
            Stocks = input.Stocks with { Medicine = Known(EquatableArray.From(new[] { new Amount(new Resource("MedicineHerbal"), (long)medicine) })) },
        };
    }

    private static ColonyModelPolicy Policy(ProjectGraph graph) => new() { MedicineTendsPerDay = Known(0.0), Projects = graph };

    private static SkillTrack Track(ColonyTrajectory trajectory) => trajectory.Skills.Single(t => t.Key == Key);

    private static SkillStock Stock(SkillLearning mechanics) =>
        SkillStock.Of(new ModelPawn { Pawn = "Anna", Skills = Known(EquatableArray.From(new[] { new WorkSkill { Name = "Medicine", Learning = mechanics } })) }, new SortedSet<string>()).Single();

    // A value that rises with the Medicine level: the level-days it stands over the horizon.
    private sealed class LevelDays(double horizon) : ITrajectoryValue
    {
        public ColonyValueResult Price(ColonyTrajectory trajectory) => new(Known(trajectory.Skills.Where(t => t.Skill == "Medicine").Sum(t =>
            (t.StoredLevel * horizon) + t.Dates.Select((d, i) => (d.StoredLevel - (i == 0 ? t.StoredLevel : t.Dates[i - 1].StoredLevel)) * (horizon - d.Day)).Sum())), []);

        public double GoalValue(GoalTerminal goal, double? completionDay) => 0;
    }

    // A value of the medicine left at the horizon, each kind at its own weight.
    private sealed class MedicineLeft(IReadOnlyDictionary<string, double> weights) : ITrajectoryValue
    {
        public ColonyValueResult Price(ColonyTrajectory trajectory) =>
            new(Known(trajectory.MedicineKinds.Select((k, i) => weights[k.Value] * trajectory.Events[^1].Medicine[i]).Sum()), []);

        public double GoalValue(GoalTerminal goal, double? completionDay) => 0;
    }

    [Fact]
    public void ALevelUpAndALevelDownInsideADayMatchForecastSkillOverTheSameXp()
    {
        // Up: 10 raw XP over 100 ticks, doubled, takes 190 XP at level 1 past its 200 to level 2 with 10 over, halfway through.
        var up = Mechanics(1, 190) with
        {
            UnsaturatedFactor = Known(2d), SaturationThreshold = Known(40_000d), LossThreshold = Known(-100d),
            Levels = [new(0, 100, 0), new(1, 200, 0), new(2, 300, 0)],
        };
        var expected = ForecastSkillPolicy.ForecastSkill(up, [new("work", 100, Known(10d), SkillXpPath.NormalTracker)], 100);
        var stock = Stock(up);
        stock.RawPerDay = 10 * Day / 100;
        stock.Advance(0, 100 / Day);
        var track = stock.Track();
        Assert.Equal(expected.Level.Value, track.StoredLevelOn(1));
        Assert.Equal(expected.Xp.Value, stock.Xp, 9);
        Assert.Equal(50 / Day, Assert.Single(track.Dates).Day, 12);

        // Down: level 1 decays 10 XP each 200 ticks, so -95 XP passes the -100 loss threshold and drops to level 0.
        var down = up with { Xp = Known(-95d), DecayInterval = Known(200), Levels = [new(0, 100, 0), new(1, 200, 10), new(2, 300, 20)] };
        expected = ForecastSkillPolicy.ForecastSkill(down, [], 200);
        stock = Stock(down);
        stock.Advance(0, 200 / Day);
        track = stock.Track();
        Assert.Equal(0, expected.Level.Value);
        Assert.Equal(expected.Level.Value, track.StoredLevelOn(1));
        Assert.Equal(expected.Xp.Value, stock.Xp, 9);
    }

    [Fact]
    public void DecayOverDaysWalksEachLevelsOwnDecay()
    {
        // A decay interval of a day: level 2 loses 200 XP a day, level 1 loses 100, level 0 nothing; the loss threshold is -100.
        var mechanics = Mechanics(2, 0) with { DecayInterval = Known((int)Day), LossThreshold = Known(-100d), Levels = [new(0, 1000, 0), new(1, 1000, 100), new(2, 1000, 200)] };

        var stock = Stock(mechanics);
        stock.Advance(0, 3);
        // Half a day to -100 at level 2, down to level 1 at 900, then 2.5 days at 100 a day.
        Assert.Equal([new SkillLevelDate(0.5, 1)], stock.Track().Dates);
        Assert.Equal(650, stock.Xp, 9);

        stock = Stock(mechanics);
        stock.Advance(0, 12);
        // Level 1 takes 10 days from 900 to -100; level 0 does not decay.
        Assert.Equal([new SkillLevelDate(0.5, 1), new SkillLevelDate(10.5, 0)], stock.Track().Dates);
        Assert.Equal(0, stock.Xp);
    }

    [Fact]
    public void AStaffedPracticeNodeBringsTheLevelDatesEarlierAndUsesUpMedicineAtItsSurgeryRate()
    {
        var input = Practising(Mechanics(0, 0), medicine: 1000, speedStat: "MedicalOperationSpeed", speed: 0.5);

        var idle = ProjectGraphTests.Run(input, new ProjectGraph());
        var practised = ColonyModel.Run(input, [.. input.Pawns.Value.Select(p => PawnCycle.Year(input, p).ToArray())], Policy(ProjectGraph.Practice(input)));

        Assert.Empty(Track(idle).Dates);
        var dates = Track(practised).Dates;
        Assert.True(dates.Count > 5);
        Assert.Equal(RequiredXp / AppliedPerDay, dates[0].Day, 6);
        Assert.Equal(1, dates[0].StoredLevel);
        // Half speed: a surgery takes 3000 ticks of the 14.1 hours, one medicine each.
        var perDay = 14.1 * GameTime.TicksPerHour * 0.5 / 1500;
        var e = practised.Events.First(e => e.Day > 1);
        Assert.Equal(1000 - (perDay * e.Day), e.Levels[(int)ModelStock.Medicine], 6);
        Assert.Equal(e.Levels[(int)ModelStock.Medicine], Assert.Single(e.Medicine), 9);
    }

    [Fact]
    public void WithoutMedicineNoSurgeryStartsAndTheStockStandsStill()
    {
        var input = Practising(Mechanics(0, 0), medicine: 0);

        var run = ColonyModel.Run(input, [.. input.Pawns.Value.Select(p => PawnCycle.Year(input, p).ToArray())], Policy(ProjectGraph.Practice(input)));

        Assert.Empty(Track(run).Dates);
    }

    [Fact]
    public void AnXpIsWorthALevelDependentValueFarFromAThresholdAndSmoothlyAcrossIt()
    {
        double PerXp(SkillLearning mechanics)
        {
            var input = Practising(mechanics);
            var marginals = ProjectMarginalValue.Of(input, Policy(ProjectGraph.Practice(input)), new LevelDays(CalendarRules.YearDays));
            return marginals.PerUnit[Key];
        }

        var far = PerXp(Mechanics(0, RequiredXp / 2));
        var below = PerXp(Mechanics(0, RequiredXp - 1));
        var above = PerXp(Mechanics(1, 1));

        // An hour of XP brings every later level date earlier by the days the practice takes to earn it, so each XP is worth about
        // the levels still to come over the applied rate.
        Assert.True(far > 0);
        Assert.Equal(8 / AppliedPerDay, below, 0.1 / AppliedPerDay);
        Assert.Equal(below, above, below * 0.02);
    }

    [Fact]
    public void AUnitOfEachMedicineKindIsWorthWhatTheValueGivesIt()
    {
        var input = ProjectGraphTests.Colony() with
        {
            Stocks = ProjectGraphTests.Colony().Stocks with
            {
                Medicine = Known(EquatableArray.From(new[] { new Amount(new Resource("MedicineIndustrial"), 5), new Amount(new Resource("MedicineHerbal"), 13) })),
            },
        };
        var weights = new Dictionary<string, double> { ["MedicineIndustrial"] = 3, ["MedicineHerbal"] = 1 };

        var marginals = ProjectMarginalValue.Of(input, Policy(new ProjectGraph()), new MedicineLeft(weights));

        Assert.Equal(3, marginals.PerUnit["medicine/MedicineIndustrial"], 9);
        Assert.Equal(1, marginals.PerUnit["medicine/MedicineHerbal"], 9);
        Assert.DoesNotContain(marginals.PerUnit.Keys, k => k.StartsWith("skill/", StringComparison.Ordinal));
    }

    [Fact]
    public void NoPracticeSlotMeansNoPracticeNode()
    {
        var input = Practising(Mechanics(0, 0));

        Assert.Empty(ProjectGraph.Practice(input with { Practice = Known(EquatableArray.From(Array.Empty<ModelPracticeSlot>())) }).Nodes);
        var unknown = ProjectGraph.Practice(input with { Practice = default });
        Assert.Empty(unknown.Nodes);
        Assert.Contains("practice_slots_unknown", unknown.Findings);
        var unread = ProjectGraph.Practice(input with { Practice = Known(EquatableArray.From(new[] { Slot() with { RawXpPerWorkTick = default } })) });
        Assert.Empty(unread.Nodes);
        Assert.Contains("practice_learning_unknown:InstallPegLeg", unread.Findings);
        Assert.Equal(ProjectKind.Practice, Assert.Single(ProjectGraph.Practice(input).Nodes).Kind);
    }

    [Fact]
    public void APrisonerMissingALimbAPegLegFitsIsAPracticeSlotAndNoOtherPrisonerIs()
    {
        var recipes = new RecipeFacts
        {
            MaterialInstalls = [new MaterialInstall { Recipe = "InstallPegLeg", Part = "PegLeg", Bodies = ["Leg"], Work = 1500, Learning = new RecipeLearning("Medicine", Known(1.6), false), SpeedStat = "MedicalOperationSpeed", Medicine = 1 }],
        };
        PrisonerFacts Prisoner(string pawn, SurgeryOperation operation, bool prisoner = true, bool dead = false) => new()
        {
            Pawn = new PawnId(pawn), Prisoner = Known(prisoner), Dead = Known(dead), Operations = Known(EquatableArray.From(new[] { operation })),
        };
        var restore = new SurgeryOperation { Kind = SurgeryKind.Restore, Recipe = Known("InstallPegLeg") };
        var removal = new SurgeryOperation { Kind = SurgeryKind.Amputate, AddedPart = Known("PegLeg") };

        var slots = RoundsColonyModel.PracticeOf(Known(EquatableArray.From(new[]
        {
            Prisoner("P1", restore), Prisoner("P2", removal), Prisoner("P3", restore, prisoner: false), Prisoner("P4", restore, dead: true),
            Prisoner("P5", new SurgeryOperation { Kind = SurgeryKind.Restore, Recipe = Known("InstallBionicLeg") }),
        })), recipes).Value;

        Assert.Equal(["P1", "P2"], slots.Select(s => s.Prisoner));
        Assert.Equal(Slot("MedicalOperationSpeed") with { Prisoner = "P2" }, slots[1]);
        Assert.False(RoundsColonyModel.PracticeOf(default, recipes).IsKnown);
        Assert.False(RoundsColonyModel.PracticeOf(Known(EquatableArray.From(new[] { Prisoner("P1", restore) with { Operations = default } })), recipes).IsKnown);
    }
}
