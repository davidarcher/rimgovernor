using RimGovernor.Native;
using System.Reflection;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class IncidentCycleTests
{
    private static readonly object NativeGate = new();
    internal static IncidentCycleTimes ReadNative(IncidentCycleReader.Parameters input, int worldSeed, int targetSeed, int now, double horizon)
    {
        lock (NativeGate) return IncidentCycleReader.Read(input, worldSeed, targetSeed, now, horizon);
    }

    [Fact]
    public void NativeSpacingMovesHitsAndAnImpossibleFixedCountProducesNoHits()
    {
        var input = new IncidentCycleReader.Parameters { OnDays = 4, OffDays = 6, Min = 2, Max = 2, MinDaysPassed = -1 };
        var unspaced = ReadNative(input, 17, 23, 0, 100);
        Assert.Equal(20, unspaced.Days.Count);
        var spaced = ReadNative(input with { SpacingDays = 3 }, 17, 23, 0, 100);
        Assert.Equal(20, spaced.Days.Count);
        Assert.All(spaced.Days.Zip(spaced.Days.Skip(1)), pair => Assert.True(pair.Second - pair.First >= 3 - 1e-9));
        Assert.Empty(ReadNative(input with { SpacingDays = 5 }, 17, 23, 0, 100).Days);
    }

    [Fact]
    public void NativeReadRestoresBothGlobalScratchAndRandomState()
    {
        lock (NativeGate)
        {
        // The native scratch is not a persisted schedule; identity matters because gameplay may retain its current list.
        var field = typeof(IncidentCycleUtility).GetField("hits", BindingFlags.Static | BindingFlags.NonPublic)!;
        var original = field.GetValue(null);
        Rand.PushState(123);
        try
        {
            var expected = Rand.Value;
            Rand.Seed = 123;
            ReadNative(new() { OnDays = 4, OffDays = 6, SpacingDays = 1, Min = 1, Max = 2 }, 17, 23, 0, 30);
            Assert.Equal(expected, Rand.Value);
            Assert.Same(original, field.GetValue(null));
        }
        finally { Rand.PopState(); }
        }
    }

    [Fact]
    public void TheProductionThreatRateUsesNativeSuccessfulHitsInsteadOfTheConfiguredCountMean()
    {
        var native = ThreatScheduleTests.Cycle(0, 4, 6, 2, 2);
        var catalog = ThreatScheduleTests.Catalog(ThreatScheduleTests.Teller("Spacing", native));
        var table = catalog.ThreatSchedule();
        var input = new IncidentCycleReader.Parameters { CompIndex = 0, OnDays = 4, OffDays = 6, Min = 2, Max = 2 };
        double Rate(float spacing)
        {
            var row = ReadNative(input with { SpacingDays = spacing }, 17, 23, 30 * 60000, 100);
            var facts = ColonyFactsCoreTests.QuietThreat with { Storyteller = "Spacing", DaysPassed = 30, Cycles = [row] };
            var schedule = ThreatSchedule.Of(table, facts);
            Assert.True(schedule.Assessed);
            return schedule.Raids.Sum(r => r.At(100));
        }
        Assert.Equal(0.1, Rate(0), 12);
        Assert.Equal(0.1, Rate(3), 12);
        Assert.Equal(0, Rate(5));
    }

    [Fact]
    public void MissingCycleOrCoverageIsARequiredObservationEvenWhenTheObservedWindowHasNoHits()
    {
        Assert.Equal("incident_cycle_unobserved:3", Assert.Throws<RequiredFactException>(() => IncidentTiming.RequireCycle([], 3)).Fact);
        IncidentTiming timed = new IncidentTiming.Scheduled(new(3, 1, []), 1);
        Assert.Equal(0, timed.PerDay(100, 1));
        Assert.Equal("incident_cycle_window_unobserved", Assert.Throws<RequiredFactException>(() => timed.PerDay(100, 2)).Fact);
        Assert.Throws<RequiredFactException>(() => new IllnessSchedule { Assessed = true, ThroughDays = 1 }.IncidentsPerDay(2));
    }

    [Fact]
    public void NativeCoverageFollowsTheRequestedHorizonAndInvalidAcceptanceDoesNotConsumeRandomness()
    {
        lock (NativeGate)
        {
            var input = new IncidentCycleReader.Parameters { OnDays = 4, OffDays = 6, Min = 1, Max = 1 };
            var row = ReadNative(input, 17, 23, 0, 75);
            Assert.Equal(75, row.ThroughDays);
            Assert.True(new IncidentTiming.Scheduled(row, 1).PerDay(0, 75) > 0);
            Rand.PushState(123);
            try
            {
                var expected = Rand.Value;
                Rand.Seed = 123;
                Assert.Throws<InvalidOperationException>(() => ReadNative(input with { AcceptAtDay = _ => -.5f }, 17, 23, 0, 2));
                Assert.Equal(expected, Rand.Value);
            }
            finally { Rand.PopState(); }
        }
    }

    [Fact]
    public void ReusingAConstantAcceptancePlateauPreservesNativeHistoryAndStrictStartGates()
    {
        var input = new IncidentCycleReader.Parameters { OnDays = 4, OffDays = 6, Min = 1, Max = 2, Accept = .5f, MinDaysPassed = 2 };
        var plain = ReadNative(input, 17, 23, 0, 75);
        var plateau = ReadNative(input with { AcceptAtDay = _ => .5f }, 17, 23, 0, 75);
        Assert.Equal(plain, plateau);
        Assert.All(plateau.Days, day => Assert.True(day > 2));
    }

    private sealed record NativeTellers(IReadOnlyDictionary<string, StorytellerDef> Values);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ThreatScheduleTable, NativeTellers> Tellers = new();

    internal static ThreatScheduleTable Table(params Def[] defs)
    {
        var table = ThreatScheduleTests.Catalog(defs).ThreatSchedule();
        Tellers.Add(table, new(defs.OfType<StorytellerDef>().ToDictionary(t => t.defName, StringComparer.Ordinal)));
        return table;
    }

    internal static IncidentCycleTimes Read(StorytellerCompProperties_OnOffCycle cycle, int index, ThreatFacts facts,
        double horizon = 60, double progress = 0, bool noTree = false)
    {
        var factor = (cycle.acceptPercentFactorPerThreatPointsCurve?.Evaluate((float)facts.RaidPoints) ?? 1)
            * (cycle.acceptPercentFactorPerProgressScoreCurve?.Evaluate((float)progress) ?? 1);
        return ReadNative(new()
        {
            CompIndex = index, Salt = index, StartDays = cycle.minDaysPassed, MinDaysPassed = cycle.minDaysPassed,
            OnDays = noTree && cycle.onDaysNoTreeConnectors != 0 ? cycle.onDaysNoTreeConnectors : cycle.onDays,
            OffDays = noTree && cycle.offDaysNoTreeConnectors != 0 ? cycle.offDaysNoTreeConnectors : cycle.offDays,
            SpacingDays = cycle.minSpacingDays, Min = cycle.numIncidentsRange.min, Max = cycle.numIncidentsRange.max,
            Accept = factor,
            AcceptAtDay = cycle.acceptFractionByDaysPassedCurve is { } curve ? day => factor * curve.Evaluate((float)day) : null,
        }, 17, 23, (int)(facts.DaysPassed * 60000), horizon);
    }

    internal static ThreatFacts Observe(ThreatScheduleTable table, ThreatFacts facts, double horizon = 60, double generatorStart = 0,
        double scale = 1, double progress = 0, bool noTree = false)
    {
        if (!double.IsFinite(facts.DaysPassed) || facts.DaysPassed < 0 || !table.Storytellers.ContainsKey(facts.Storyteller)) return facts;
        var teller = Tellers.GetValue(table, _ => throw new InvalidOperationException("Native storyteller fixture was not registered.")).Values[facts.Storyteller];
        var decoded = table.Storytellers[facts.Storyteller];
        var needed = decoded.Cycles.Select(c => c.CompIndex).Concat(decoded.Generators.Select(g => g.CompIndex)).ToHashSet();
        var factor = IncidentCycleReader.GeneratorCountFactor((float)scale);
        var rows = new List<IncidentCycleTimes>();
        for (var index = 0; index < teller.comps.Count; index++)
        {
            var comp = teller.comps[index];
            if (!comp.Enabled || !needed.Contains(index)) continue;
            if (comp is StorytellerCompProperties_OnOffCycle cycle) rows.Add(Read(cycle, index, facts, horizon, progress, noTree));
            if (comp is StorytellerCompProperties_ThreatsGenerator { parms: { } g }) rows.Add(ReadNative(new()
            {
                CompIndex = index, Salt = g.randSeed, StartDays = (float)generatorStart, MinDaysPassed = comp.minDaysPassed,
                OnDays = g.onDays, OffDays = g.offDays, SpacingDays = g.minSpacingDays,
                Min = g.numIncidentsRange.min * factor, Max = g.numIncidentsRange.max * factor,
            }, 17, 23, (int)(facts.DaysPassed * 60000), horizon));
        }
        return facts with { Cycles = [.. rows] };
    }
}
