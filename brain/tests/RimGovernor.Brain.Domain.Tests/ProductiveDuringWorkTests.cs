using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Generation;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
namespace RimGovernor.Brain.Domain.Tests;

public sealed class ProductiveDuringWorkTests
{
    private static ProductiveSkillWork Work(string skill, double amount, double speed, bool before = false, bool fixedDuration = false)
        => new(ProductiveLearningTests.Work().Learning with { NextMidnightReset = Known(500), ValidForTicks = Known(499),
                Levels = [new(0, 100, 0), new(1, 100, 0), new(2, 10000, 0)] },
            new(skill, Known(.1), true), Known(amount), [new(0, Known(speed)), new(1, Known(speed)), new(2, Known(speed * 2))], "native-test")
        { LearnBeforeWork = before, FixedDuration = fixedDuration };

    [Fact]
    public void GeneratedResearchBlockKeepsRealLearningAndIncreasedPointsAcrossMidnight()
    {
        var worker = new ResearchWorker("pawn", "bench", new(1, 1), "research", "giver", "job", .5)
            { SkillWork = Work("Intellectual", 10000, .5, fixedDuration: true) };
        var read = new ResearchActivityRead { Workers = [worker], Findings = [] };
        Assert.Equal(read, WireJson.Deserialize<ResearchActivityRead>(WireJson.Serialize(read)));
        var generated = ResearchMethodCandidates.Generate(read, GameTime.TicksPerDay, new([], [], [], []), GameTime.TicksPerDay);
        var result = ForecastProductiveWorkPolicy.ForecastProductiveWork(Assert.Single(generated.Options), [], 0);
        Assert.Equal(GameTime.TicksPerHour, result.WorkTicks.Value);
        var learning = Assert.IsType<ForecastProductiveLearning>(result.Evidence);
        Assert.Equal(250, learning.After.AppliedGain.Value, 8);
        Assert.Equal(2, learning.After.StoredLevel.Value);
        Assert.True(learning.WorkAmount > GameTime.TicksPerHour * worker.PointsPerTick);
        Assert.True(ForecastProductiveWorkPolicy.CompletionTick(learning, 100).Value < 200);
        Assert.Single(learning.Callbacks);
        Assert.Equal(GameTime.TicksPerHour, learning.Callbacks[0].Count);
        var terminal = ForecastSkillPolicy.ForecastTerminalSkill(worker.SkillWork.Learning, learning.Callbacks, GameTime.TicksPerDay);
        Assert.Equal(learning.After.StoredLevel, terminal.StoredLevel);
        Assert.Equal(learning.After.Xp.Value, terminal.Xp.Value, 8);
    }

    [Fact]
    public void GeneratedSowingLevelsBeforeWorkAndDiscardsEachCellsExcess()
    {
        var worker = new SowingWorker("pawn", "zone", "giver", Known(0d), Known(100d), [new(1, 1), new(1, 2)])
            { SkillWork = Work("Plants", 2, .01, before: true) };
        var generated = SowingMethodCandidates.Generate(new() { Known = true, Workers = [worker] }, 0, 1000, new([], [], [], []));
        var option = Assert.Single(generated.Options);
        var result = ForecastProductiveWorkPolicy.ForecastProductiveWork(option, [], 0);
        var learning = Assert.IsType<ForecastProductiveLearning>(result.Evidence);
        Assert.True(result.WorkTicks.Value < 200);
        Assert.Equal(2, learning.WorkAmount, 8);
        var first = ForecastProductiveWorkPolicy.CompletionTick(learning, 1).Value;
        var second = ForecastProductiveWorkPolicy.CompletionTick(learning, 2).Value;
        Assert.True(first < 100 && second > first);
        Assert.Equal(result.WorkTicks.Value, second);
        Assert.Equal(result.WorkTicks.Value * .1, learning.After.AppliedGain.Value, 8);
        // The callback that crosses a level changes sowing's work in that very callback.
        var after = ForecastProductiveWorkPolicy.ForecastProductiveWork(option with { SkillWork = option.SkillWork! with { LearnBeforeWork = false } }, [], 0);
        Assert.True(after.WorkTicks.Value >= result.WorkTicks.Value);
    }

    [Fact]
    public void NativeSaturationUsesEachCallbackAndResetOccursAfterWorkBeforeDecay()
    {
        var mechanics = Work("Intellectual", 1, 1).Learning with { StoredLevel = Known(1), Xp = Known(0d),
            XpSinceMidnight = Known(1001d), NextMidnightReset = Known(10), NextDecay = Known(10),
            Levels = [new(0, 100, 0), new(1, 1000, 2), new(2, 10000, 0)] };
        SkillXpEvent[] callbacks = [new("work", 10, Known(10d), SkillXpPath.NormalTracker, BeforeDecay: true, Count: 2, Interval: 1)];
        var result = ForecastSkillPolicy.ForecastSkill(mechanics, callbacks, 11);
        Assert.Equal(12, result.AppliedGain.Value); // saturated callback at 10, reset, full callback at 11.
        Assert.Equal(2, result.DecayLoss.Value);
        Assert.Equal(10, result.Xp.Value);
    }

    [Fact]
    public void CallbackCadenceIsPreservedAndCompressedRunsEqualIndividualCallbacks()
    {
        var work = Work("Plants", 1, .01, before: true) with { CallbackIntervalTicks = 15, CallbackPhase = 7, CallbackElapsedTicks = 8 };
        var result = ForecastProductiveWorkPolicy.ForecastProductiveWork(new("sow", "pawn", Known(100d), Known(0d)) { SkillWork = work }, [], 0);
        var learning = Assert.IsType<ForecastProductiveLearning>(result.Evidence);
        var expanded = learning.Callbacks.SelectMany(e => Enumerable.Range(0, e.Count).Select(i => e with
            { Id = e.Id + "/" + i, Tick = e.Tick + i * e.Interval, Count = 1, Interval = 0 })).ToArray();
        var projected = ForecastSkillPolicy.ForecastSkill(work.Learning, expanded, (int)result.WorkTicks.Value);
        Assert.Equal(learning.After, projected);
        Assert.All(expanded, e => Assert.Equal(7, e.Tick % 15));
        Assert.All(expanded, e => Assert.Equal(1.5, e.RawXp.Value, 8));
        Assert.Equal(7, expanded[0].Tick);
    }
    [Fact]
    public void ResearchStopsLearningAtProjectCompletionInsideItsBlock()
    {
        var option = new ForecastActivityOption("research", "pawn", Known(2500d), Known(0d))
            { SkillWork = Work("Intellectual", 10, 1, fixedDuration: true) };
        var result = ForecastProductiveWorkPolicy.ForecastProductiveWork(option, [], 0);
        Assert.Equal(10, result.WorkTicks.Value);
        Assert.Equal(10, result.Evidence!.WorkAmount);
        Assert.Equal(1, result.Evidence.After.AppliedGain.Value, 8);
    }

    [Fact]
    public void PerTargetStagesPreserveCutLearningAndMiningPickTimer()
    {
        var work = Work("Mining", 2, .01, before: true) with
        {
            Stages = [new(1, 1, Known(1d), 1, true), new(1, 1, Known(0d), 1, true)]
        };
        var result = ForecastProductiveWorkPolicy.ForecastProductiveWork(
            new("mine", "pawn", Known(200d), Known(0d)) { SkillWork = work }, [], 0);
        // XP raises the first pick's skill; its timer remains the original100ticks. The next target uses the new rate.
        Assert.Equal(100, ForecastProductiveWorkPolicy.CompletionTick(result.Evidence!, 1).Value);
        Assert.Equal(150, result.WorkTicks.Value);
        Assert.Equal(100, result.Evidence!.After.AppliedGain.Value);
        Assert.Equal(0, result.Evidence.Callbacks[^1].RawXp.Value);
    }

    [Fact]
    public void PreparationPaysNoXpAndTerminalCountersResumeIdentically()
    {
        var work = Work("Crafting", 10, 1, before: true) with { PreparationTicks = 20 };
        var result = ForecastProductiveWorkPolicy.ForecastProductiveWork(
            new("tailor", "pawn", Known(30d), Known(0d)) { SkillWork = work }, [], 0);
        Assert.Equal(30, result.WorkTicks.Value);
        Assert.Equal(1, result.Evidence!.After.AppliedGain.Value, 8);
        var first = ForecastSkillPolicy.ForecastSkill(work.Learning, result.Evidence.Callbacks, 30);
        var resumed = ForecastSkillPolicy.ForecastSkill(first.TerminalLearning!, [], 600);
        var continuous = ForecastSkillPolicy.ForecastSkill(work.Learning, result.Evidence.Callbacks, 630);
        Assert.Equal(continuous.StoredLevel, resumed.StoredLevel);
        Assert.Equal(continuous.Xp, resumed.Xp);
        Assert.Equal(continuous.TerminalLearning, resumed.TerminalLearning);
    }
    [Fact]
    public void TwoResearchersStopTogetherAndCarryOnlyEarnedXpIntoFollowingWork()
    {
        var work = Work("Intellectual", 100, 1, fixedDuration: true) with
        { Rates = [new(0, Known(1d)), new(1, Known(1d)), new(2, Known(1d))] };
        var problem = new ForecastActivityProblem
        {
            Activities = [new("a", "lab", "lab", []) { ProjectProgress = new("project", 1) },
                new("b", "lab", "lab", []) { ProjectProgress = new("project", 1) }],
            Options = [new("a", "one", Known(2500d), Known(0d)) { SkillWork = work with { CallbackOrder = Known(1) } },
                new("b", "two", Known(2500d), Known(0d)) { SkillWork = work with { CallbackOrder = Known(2) } }],
            Pawns = [new("one", "lab", Known<EquatableArray<ForecastTimeWindow>>([new(0, 3000)]), Known(1d)),
                new("two", "lab", Known<EquatableArray<ForecastTimeWindow>>([new(0, 3000)]), Known(1d))],
            Deadline = Known(3000d), AlternativeLaborCost = Known(10000d)
        };
        var result = ForecastActivities.CompareActivityPlans(problem).Selected!;
        Assert.Equal(50, result.CompletedAt);
        Assert.Equal(100, result.LaborCost);
        Assert.All(result.Allocations, a =>
        {
            Assert.Equal(50, a.Learning!.WorkAmount);
            Assert.Equal(5, a.Learning.After.AppliedGain.Value, 8);
            Assert.All(a.Learning.Callbacks, e => Assert.True(e.LastTick <= 50));
        });
    }
    [Theory]
    [InlineData(1d, 0d)]
    [InlineData(3d, .1)]
    public void NativeResearchOrderSuppressesCallbacksAfterCompletionAndConservesOvershoot(double remaining, double secondXp)
    {
        var work = Work("Intellectual", remaining, 2, fixedDuration: true);
        var problem = new ForecastActivityProblem
        {
            Activities = [new("a", "lab", "lab", []) { ProjectProgress = new("project", 2) },
                new("b", "lab", "lab", []) { ProjectProgress = new("project", 2) }],
            Options = [new("a", "one", Known(2500d), Known(0d)) { SkillWork = work with { CallbackOrder = Known(1) } },
                new("b", "two", Known(2500d), Known(0d)) { SkillWork = work with { CallbackOrder = Known(2) } }],
            Pawns = [new("one", "lab", Known<EquatableArray<ForecastTimeWindow>>([new(0, 3000)]), Known(1d)),
                new("two", "lab", Known<EquatableArray<ForecastTimeWindow>>([new(0, 3000)]), Known(1d))],
            Deadline = Known(3000d), AlternativeLaborCost = Known(10000d)
        };
        var result = ForecastActivities.CompareActivityPlans(problem).Selected!;
        Assert.Equal(1, result.CompletedAt);
        Assert.Equal(remaining, result.Allocations.Sum(a => a.Learning!.WorkAmount));
        Assert.Equal(.1, result.Allocations.Single(a => a.Pawn == "one").Learning!.After.AppliedGain.Value, 8);
        Assert.Equal(secondXp, result.Allocations.Single(a => a.Pawn == "two").Learning!.After.AppliedGain.Value, 8);
        Assert.Equal(remaining, result.Allocations.SelectMany(a => a.Learning!.WorkSegments).Sum(s => (s.End - s.Start) * s.WorkPerTick));
    }
    [Fact]
    public void RetainedNativeCounterPaysExactFirstAndSecondCallbackDeltasAfterCadenceChange()
    {
        var work = Work("Plants", 20, 1, before: true) with
        {
            CallbackIntervalTicks = 15, CallbackPhase = 7, CallbackElapsedTicks = 13,
            Learning = Work("Plants", 20, 1).Learning with { Xp = Known(0d) }
        };
        var result = ForecastProductiveWorkPolicy.ForecastProductiveWork(
            new("sow", "pawn", Known(20d), Known(0d)) { SkillWork = work }, [], 0);
        Assert.Equal(7, result.WorkTicks.Value);
        Assert.Equal(20, result.Evidence!.WorkAmount);
        Assert.Collection(result.Evidence.Callbacks,
            e => { Assert.Equal(2, e.Tick); Assert.Equal(1.5, e.RawXp.Value); },
            e => { Assert.Equal(7, e.Tick); Assert.Equal(.5, e.RawXp.Value); });

        Assert.Equal(2, result.Evidence.After.AppliedGain.Value);
    }
}
