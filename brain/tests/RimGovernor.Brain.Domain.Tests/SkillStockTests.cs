using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3634: the tier-2 skill stock and the medicine stock by kind, and their per-unit marginals; #3344: one XP is worth the value of one
// level more over the XP that level takes.
public sealed class SkillStockTests
{
    private const double Day = GameTime.TicksPerDay;

    private const double RequiredXp = 100_000;

    private const string Key = "skill/Anna/Medicine";

    // Twenty-one levels of 100 000 XP without decay.
    internal static SkillLearning Mechanics(int stored, double xp) => new()
    {
        Disabled = false, TrackerAllowsLearning = true, StoredLevel = stored, Aptitude = 0, Xp = xp,
        XpSinceMidnight = 0d, DirectFactor = 1d, UnsaturatedFactor = 1d, SaturationThreshold = 4000d,
        SaturatedFactor = .2, LossThreshold = -1000d, DecayInterval = 200, NextDecay = 200, ValidForTicks = 1000,
        NextMidnightReset = 1000, TrackerIntervalTicks = 1, TrackerPhase = 0, TrackerElapsedTicks = 0,
        Levels = EquatableArray.From(Enumerable.Range(0, 21).Select(l => new SkillLevelMechanics(l, RequiredXp, 0))),
        Practice = new SkillPractice.None(),
    };

    /// <summary>The pawn with a Medicine skill on the mechanics, and each of <paramref name="others"/> on the same mechanics.</summary>
    internal static ModelPawn WithMedicine(ModelPawn pawn, SkillLearning mechanics, params string[] others) => pawn with
    {
        Skills = EquatableArray.From(others.Prepend("Medicine").Select(name => new WorkSkill { Name = name, Learning = mechanics })),
    };

    /// <summary>Anna alone, with the skills of <see cref="WithMedicine"/>.</summary>
    private static ColonyModelInput Anna(SkillLearning mechanics, params string[] others)
    {
        var input = ProjectGraphTests.Colony();
        return input with { Pawns = EquatableArray.From(new[] { WithMedicine(ProjectGraphTests.Pawn("Anna", null, null), mechanics, others) }) };
    }

    private static ColonyModelPolicy Policy(ProjectGraph graph) => new() { MedicineTendsPerDay = Known(0.0), Projects = graph };

    private static SkillStock Stock(SkillLearning mechanics) =>
        SkillStock.Of(new ModelPawn { Pawn = "Anna", Skills = EquatableArray.From(new[] { new WorkSkill { Name = "Medicine", Learning = mechanics } }), Consciousness = 1.0, Moving = 1.0, Manipulation = 1.0, Sight = 1.0, Talking = 1.0, ComfortableMin = 16.0, ComfortableMax = 26.0, ResearchSpeed = 1.0, PlantWorkSpeed = 1.0, Hunts = false }, new SortedSet<string>()).Single();

    // A value that rises with the Medicine level: the level-days it stands over the horizon.
    internal sealed class LevelDays(double horizon) : ITrajectoryValue
    {
        /// <summary>The runs priced.</summary>
        internal int Priced { get; private set; }

        public bool Reads(SkillTrack track) => track.Skill == "Medicine";

        public ColonyValueResult Price(ColonyTrajectory trajectory)
        {
            Priced++;
            return Value(trajectory);
        }


        private ColonyValueResult Value(ColonyTrajectory trajectory) => new(Known(trajectory.Skills.Where(t => t.Skill == "Medicine").Sum(t =>
            (t.StoredLevel * horizon) + t.Dates.Select((d, i) => (d.StoredLevel - (i == 0 ? t.StoredLevel : t.Dates[i - 1].StoredLevel)) * (horizon - d.Day)).Sum())), []);

        public double GoalValue(GoalTerminal goal, double? completionDay) => 0;
    }

    // A value of the medicine left at the horizon, each kind at its own weight.
    private sealed class MedicineLeft(IReadOnlyDictionary<string, double> weights) : ITrajectoryValue
    {
        public bool Reads(SkillTrack track) => false;

        public ColonyValueResult Price(ColonyTrajectory trajectory) =>
            new(Known(trajectory.MedicineKinds.Select((k, i) => weights[k.Value] * trajectory.Events[^1].Medicine[i]).Sum()), []);

        public double GoalValue(GoalTerminal goal, double? completionDay) => 0;
    }

    [Fact]
    public void ALevelDownInsideADayMatchesForecastSkill()
    {
        // Level 1 decays 10 XP each 200 ticks, so -95 XP passes the -100 loss threshold and drops to level 0.
        var down = Mechanics(1, -95) with { LossThreshold = -100d, Levels = [new(0, 100, 0), new(1, 200, 10), new(2, 300, 20)] };
        var expected = ForecastSkillTests.Projected(ForecastSkillPolicy.ForecastSkill(down, [], 200));
        var stock = Stock(down);
        stock.Advance(0, 200 / Day);
        var track = stock.Track();
        Assert.Equal(0, expected.Level);
        Assert.Equal(expected.Level, track.StoredLevelOn(1));
        Assert.Equal(expected.Xp, stock.Xp, 9);
    }

    [Fact]
    public void DecayOverDaysWalksEachLevelsOwnDecay()
    {
        // A decay interval of a day: level 2 loses 200 XP a day, level 1 loses 100, level 0 nothing; the loss threshold is -100.
        var mechanics = Mechanics(2, 0) with { DecayInterval = (int)Day, LossThreshold = -100d, Levels = [new(0, 1000, 0), new(1, 1000, 100), new(2, 1000, 200)] };

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

    private static double PerXp(ColonyModelInput input, string key = Key) =>
        ProjectMarginalValue.Of(input, Policy(new ProjectGraph()), new LevelDays(CalendarRules.YearDays)).PerUnit[key];

    [Fact]
    public void AnXpIsWorthTheValueOfTheNextLevelOverTheXpItTakes()
    {
        // Without decay one level more stands the whole year: a year of level-days over the level's 100 000 XP, wherever the XP toward it is.
        Assert.Equal(CalendarRules.YearDays / RequiredXp, PerXp(Anna(Mechanics(3, 0))), 12);
        Assert.Equal(CalendarRules.YearDays / RequiredXp, PerXp(Anna(Mechanics(3, RequiredXp - 1))), 12);
        Assert.Equal(CalendarRules.YearDays / RequiredXp, PerXp(Anna(Mechanics(3, -500))), 12);
    }

    [Fact]
    public void TheMarginalScalesInverselyWithTheXpTheLevelTakes()
    {
        var cheap = Mechanics(3, 0);
        var dear = cheap with { Levels = EquatableArray.From(cheap.Levels.Select(l => l.Level == 3 ? l with { RequiredXp = 4 * RequiredXp } : l)) };

        Assert.Equal(PerXp(Anna(cheap)) / 4, PerXp(Anna(dear)), 12);
    }

    [Fact]
    public void ASkillWhoseNextLevelChangesNothingAndATopLevelSkillAreWorthNothing()
    {
        Assert.Equal(0, PerXp(Anna(Mechanics(3, 0), "Cooking"), "skill/Anna/Cooking"));
        Assert.Equal(0, PerXp(Anna(Mechanics(20, 0))));
    }

    [Fact]
    public void ARaisedStockStartsALevelHigherAndDecaysAtThatLevelsRate()
    {
        // Level 2 loses 200 XP a day from 0 to the -100 threshold in half a day; raised from level 1, the stock starts at level 2.
        var mechanics = Mechanics(1, 0) with { DecayInterval = (int)Day, LossThreshold = -100d, Levels = [new(0, 1000, 0), new(1, 1000, 100), new(2, 1000, 200)] };

        var stock = Stock(mechanics);
        stock.Raise();
        stock.Advance(0, 1);
        Assert.Equal([new SkillLevelDate(0, 2), new SkillLevelDate(0.5, 1)], stock.Track().Dates);
        Assert.Equal(new SkillNextLevel.Takes(1000), stock.Track().NextLevel);
        Assert.Equal(new SkillNextLevel.Top(), Stock(mechanics with { StoredLevel = 2 }).Track().NextLevel);
    }

    [Fact]
    public void AUnitOfEachMedicineKindIsWorthWhatTheValueGivesIt()
    {
        var input = ProjectGraphTests.Colony() with
        {
            Stocks = ProjectGraphTests.Colony().Stocks with
            {
                Medicine = EquatableArray.From(new[] { new Amount(new Resource("MedicineIndustrial"), 5), new Amount(new Resource("MedicineHerbal"), 13) }),
            },
        };
        var weights = new Dictionary<string, double> { ["MedicineIndustrial"] = 3, ["MedicineHerbal"] = 1 };

        var marginals = ProjectMarginalValue.Of(input, Policy(new ProjectGraph()), new MedicineLeft(weights));

        Assert.Equal(3, marginals.PerUnit["medicine/MedicineIndustrial"], 9);
        Assert.Equal(1, marginals.PerUnit["medicine/MedicineHerbal"], 9);
        Assert.DoesNotContain(marginals.PerUnit.Keys, k => k.StartsWith("skill/", StringComparison.Ordinal));
    }
}
