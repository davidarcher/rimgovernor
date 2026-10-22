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
        var worker = new ResearchWorker("pawn", "bench", new(1, 1), "research", "giver", "job", Known(.5))
            { SkillWork = Work("Intellectual", 1, .5, fixedDuration: true) };
        var read = new ResearchActivityRead { Known = true, Workers = [worker] };
        Assert.Equal(read, WireJson.Deserialize<ResearchActivityRead>(WireJson.Serialize(read)));
        var generated = ResearchMethodCandidates.Generate(read, GameTime.TicksPerDay, new([], [], [], []), GameTime.TicksPerDay);
        var result = ForecastProductiveWorkPolicy.ForecastProductiveWork(Assert.Single(generated.Options), [], 0);
        Assert.Equal(GameTime.TicksPerHour, result.WorkTicks.Value);
        var learning = Assert.IsType<ForecastProductiveLearning>(result.Evidence);
        Assert.Equal(250, learning.After.AppliedGain.Value, 8);
        Assert.Equal(2, learning.After.StoredLevel.Value);
        Assert.True(learning.WorkAmount > GameTime.TicksPerHour * worker.PointsPerTick.Value);
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
        var work = Work("Plants", 1, .01, before: true) with { CallbackIntervalTicks = 15, CallbackPhase = 7 };
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
}
