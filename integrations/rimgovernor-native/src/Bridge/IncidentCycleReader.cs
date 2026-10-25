using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RimGovernor.Brain.GameFacts.Observations;
using RimWorld;
using Verse;

namespace RimGovernor.Native;

/// <summary>Reads the game's own seeded hit lists; callers run on the game thread. No schedule or RNG state is retained.</summary>
public static class IncidentCycleReader
{
    // IncidentCycleUtility's private initial previous-hit literal has no native symbol (verified installed 1.7 IL).
    private const int InitialPreviousHit = -9999999;
    // ThreatsGenerator is static, so its private curve has no typed accessor route. Mirror its native float arithmetic here.
    private static readonly SimpleCurve CountCurve = new([
        new CurvePoint(0, 0.1f), new CurvePoint(0.3f, 0.5f), new CurvePoint(0.6f, 0.8f), new CurvePoint(1, 1),
        new CurvePoint(1.55f, 1.1f), new CurvePoint(2.2f, 1.2f), new CurvePoint(10, 2)]);

    public static float GeneratorCountFactor(float scale) => CountCurve.Evaluate(scale);

    public sealed record Parameters
    {
        public int CompIndex { get; init; }
        public int Salt { get; init; }
        public float StartDays { get; init; }
        public float MinDaysPassed { get; init; }
        public float OnDays { get; init; }
        public float OffDays { get; init; }
        public float SpacingDays { get; init; }
        public float Min { get; init; }
        public float Max { get; init; }
        public float Accept { get; init; } = 1;
        public Func<double, float>? AcceptAtDay { get; init; }
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "hits")]
    private static extern ref List<int> Hits(IncidentCycleUtility? target);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "GenerateHitList")]
    private static extern void Generate(IncidentCycleUtility? target, int seed, int start, int length,
        float minIncidents, float maxIncidents, int minSpacingIntervals, float acceptFraction, int fixedHit);

    public static IncidentCycleTimes Read(Parameters input, int worldSeed, int targetSeed, int nowTicks, double horizonDays)
    {
        if (!double.IsFinite(horizonDays) || horizonDays < 0) throw new ArgumentOutOfRangeException(nameof(horizonDays));
        if (nowTicks < 0 || nowTicks + horizonDays * GenDate.TicksPerDay > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(horizonDays));
        int Intervals(float days)
        {
            var value = days * 60f;
            if (!float.IsFinite(value) || value < int.MinValue || value >= int.MaxValue)
                throw new InvalidOperationException("Native incident interval is outside the game clock.");
            return UnityEngine.Mathf.RoundToInt(value);
        }
        var start = Intervals(input.StartDays);
        var on = Intervals(input.OnDays);
        var length = checked(on + Intervals(input.OffDays));
        if (on <= 0 || input.OffDays < 0 || length <= 0 || !float.IsFinite(input.Min) || !float.IsFinite(input.Max)
            || input.Min < 0 || input.Max < input.Min || input.Max >= int.MaxValue
            || !float.IsFinite(input.MinDaysPassed) || !float.IsFinite(input.SpacingDays) || input.SpacingDays < 0)
            throw new InvalidOperationException("Invalid native incident cycle parameters.");
        var spacing = Intervals(input.SpacingDays);
        float Acceptance(float value) => float.IsFinite(value) && value >= 0 ? value
            : throw new InvalidOperationException("Invalid native incident acceptance.");
        var accept = Acceptance(input.Accept);
        if (horizonDays == 0) return new(input.CompIndex, 0, []);
        var lastCycle = checked((int)Math.Ceiling((nowTicks / 1000.0 + horizonDays * 60 - start) / length) - 1);
        if (lastCycle < 0) return new(input.CompIndex, horizonDays, []);
        checked { _ = lastCycle * length + on; }
        // Native placement shifts each hit by spacing and uses signed game intervals throughout.
        var count = (long)Math.Ceiling(input.Max);
        var latest = Math.Max(0L, (long)lastCycle * length) + on + count * spacing + count * count;
        if (latest - InitialPreviousHit > int.MaxValue || (long)start + latest > int.MaxValue)
            throw new InvalidOperationException("Native incident placement is outside the game clock.");
        var intervalAcceptances = new List<(int Interval, float Accept)>();
        if (input.AcceptAtDay is { } evaluate)
        {
            var first = (int)Math.Floor(nowTicks / 1000.0) + 1;
            var end = nowTicks / 1000.0 + horizonDays * 60;
            for (var interval = first; interval < end; interval++)
            {
                var nativeDay = interval * 1000 / 60000f;
                if (interval >= start && nativeDay > input.MinDaysPassed)
                    intervalAcceptances.Add((interval, Acceptance(evaluate(nativeDay))));
            }
        }
        var original = Hits(null);
        var local = new List<int>();
        var days = new List<double>();
        var fixedHit = InitialPreviousHit;
        Rand.PushState();
        try
        {
            Hits(null) = local;
            if (input.AcceptAtDay is not null)
            {
                // Native regeneration depends on the interval's acceptance, not its position within the window.
                // Reuse one replay for intervals with identical native float acceptance (including curve plateaus).
                foreach (var group in intervalAcceptances.GroupBy(i => i.Accept))
                {
                    var ages = group.Select(i => i.Interval - start).ToHashSet();
                    var throughCycle = ages.Max() / length;
                    fixedHit = InitialPreviousHit;
                    local.Clear();
                    for (var cycle = 0; cycle <= throughCycle; cycle++)
                    {
                        if (local.Count > 0) fixedHit = local[^1];
                        local.Clear();
                        Generate(null, Gen.HashCombineInt(worldSeed, targetSeed, input.Salt, cycle), cycle * length, on,
                            input.Min, input.Max, spacing, group.Key, fixedHit);
                        foreach (var hit in local.Where(ages.Contains))
                        {
                            if (hit < (cycle + 1) * length) days.Add(((start + hit) * 1000.0 - nowTicks) / 60000);
                        }
                    }
                }
                return new(input.CompIndex, horizonDays, [.. days.Order()]);
            }
            for (var cycle = 0; cycle <= lastCycle; cycle++)
            {
                if (local.Count > 0) fixedHit = local[^1];
                local.Clear();
                var seed = Gen.HashCombineInt(worldSeed, targetSeed, input.Salt, cycle);
                Generate(null, seed, cycle * length, on, input.Min, input.Max, spacing, accept, fixedHit);
                foreach (var hit in local)
                {
                    // A hit at the next cycle boundary is never visited for this list: native regenerates the next list then.
                    if (hit >= (cycle + 1) * length) continue;
                    var after = ((start + hit) * 1000.0 - nowTicks) / 60000;
                    if ((start + hit) * 1000 / 60000f <= input.MinDaysPassed) continue;
                    if (after > 0 && after < horizonDays) days.Add(after);
                }
            }
        }
        finally
        {
            Hits(null) = original;
            Rand.PopState();
        }
        return new(input.CompIndex, horizonDays, [.. days.Order()]);
    }
}
