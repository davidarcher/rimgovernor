using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

internal sealed record AttributedMemoryRead(string Other, SocialMemoryDefRead Memory, double Age);

/// <summary>Disposable expected memory cohorts for one pawn. Global and same-other limits share this owner.
/// Arrivals use the nursery's conditional Poisson approximation; no colony state or observed memory is changed.</summary>
internal sealed class ForecastSocialMemories(IEnumerable<AttributedMemoryRead> observed)
{
    internal static IEnumerable<AttributedMemoryRead> Observed(IEnumerable<NeedMemory> memories, IReadOnlySet<string> defs) =>
        memories.Where(m => defs.Contains(m.Id)).Select(m => m.Group is { } group
            ? new AttributedMemoryRead(group.Other, new(m.Id, group.RawOffset, group.DurationTicks, m.StackLimit,
                group.SamePawnLimit, group.Multiplier, group.FadeStarts) { Label = group.Label, Unprojected = group.Unprojected }, group.Age)
            : throw new RequiredFactException("live memory group:" + m.Id));
    private readonly AttributedMemoryRead[] initial = observed.ToArray();
    private readonly List<Arrival> arrivals = [];
    private sealed record Arrival(string Other, SocialMemoryDefRead Memory, double Start, double End, double Rate);

    internal void Gain(string other, SocialMemoryDefRead memory, double start, double end, double rate)
    {
        if (!double.IsFinite(rate) || rate < 0 || !double.IsFinite(start) || !double.IsFinite(end) || end <= start)
            throw new RequiredFactException("social memory arrival:" + memory.Def);
        if (rate <= 0) return;
        var index = arrivals.FindLastIndex(a => a.Other == other && a.Memory.Def == memory.Def);
        if (index >= 0 && arrivals[index] is { } previous && previous.End == start && previous.Rate == rate && previous.Memory == memory)
            arrivals[index] = previous with { End = end };
        else arrivals.Add(new(other, memory, start, end, rate));
    }

    internal double Offset(string other, double at, bool social)
    {
        double result = 0;
        foreach (var def in initial.Select(m => m.Memory.Def).Concat(arrivals.Select(a => a.Memory.Def)).Distinct(StringComparer.Ordinal))
        {
            var sum = Group(def, other, at, social);
            // ThoughtHandler.OpinionOffsetOfGroup: round each signed group, preserving a nonzero sign.
            result += social ? RoundGroup(sum) : sum;
        }
        return result;
    }

    /// <summary>The original additive memory term already retained in ForecastMoodResponse; remove it once before replacing it.</summary>
    internal double ObservedAdditive(double at) => initial.Sum(m => m.Memory.Offset * Fade(m.Memory, m.Age + at));

    internal double AllOffsets(double at) => initial.Select(m => m.Other).Concat(arrivals.Select(a => a.Other))
        .Distinct(StringComparer.Ordinal).Sum(other => Offset(other, at, false));

    internal double MoodChange(double at) => AllOffsets(at) - AllOffsets(0) - ObservedAdditive(at) + ObservedAdditive(0);

    internal static int RoundGroup(double sum)
    {
        var rounded = (int)Math.Round(sum, MidpointRounding.ToEven);
        return sum == 0 ? 0 : sum > 0 ? Math.Max(rounded, 1) : Math.Min(rounded, -1);
    }

    /// <summary>Native-cap Poisson count distribution. Tail groups all counts at/above limit for a latest-N pool.</summary>
    internal static double[] PoissonCounts(double mean, int limit, bool tail)
    {
        if (!double.IsFinite(mean) || mean < 0 || limit < 0) throw new RequiredFactException("memory Poisson count");
        var result = new double[limit + 1];
        var pmf = Math.Exp(-mean);
        for (var n = 0; n <= limit; n++) { result[n] = pmf; pmf *= mean / (n + 1); }
        if (tail) result[limit] = Math.Max(0, 1 - result.Take(limit).Sum());
        return result;
    }

    private double Group(string def, string other, double at, bool social)
    {
        double result = 0;
        var younger = new Dictionary<string, double>(StringComparer.Ordinal);
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cohort in arrivals.Where(a => a.Memory.Def == def && a.Start < at)
            .GroupBy(a => (a.Start, a.End)).OrderByDescending(g => g.Key.Start))
        {
            var end = Math.Min(at, cohort.Key.End);
            var duration = end - cohort.Key.Start;
            var counts = cohort.GroupBy(a => a.Other).ToDictionary(g => g.Key, g => g.Sum(a => a.Rate) * duration);
            foreach (var arrival in cohort.Where(a => a.Other == other))
            {
                var count = arrival.Rate * duration;
                result += count * arrival.Memory.Offset * Integrate(s => Fade(arrival.Memory, at - end + s * duration)
                    * Weight(arrival.Memory, other, younger.Keys.Concat(counts.Keys).Distinct(StringComparer.Ordinal)
                        .ToDictionary(id => id, id => younger.GetValueOrDefault(id) + counts.GetValueOrDefault(id) * s), ranks, social));
            }
            foreach (var (id, count) in counts) younger[id] = younger.GetValueOrDefault(id) + count;
        }
        foreach (var memory in initial.Where(m => m.Memory.Def == def).OrderBy(m => m.Age))
        {
            if (memory.Other == other)
                result += memory.Memory.Offset * Fade(memory.Memory, memory.Age + at)
                    * Weight(memory.Memory, other, younger, ranks, social);
            ranks[memory.Other] = ranks.GetValueOrDefault(memory.Other) + 1;
        }
        return result;
    }
    private static double Integrate(Func<double, double> value)
    {
        var a = value(0); var b = value(.5); var c = value(1);
        return Part(0, 1, a, b, c, (a + 4 * b + c) / 6, 0);
        double Part(double left, double right, double from, double middle, double to, double whole, int depth)
        {
            var mid = (left + right) / 2;
            var first = value((left + mid) / 2); var last = value((mid + right) / 2);
            var lo = (mid - left) * (from + 4 * first + middle) / 6;
            var hi = (right - mid) * (middle + 4 * last + to) / 6;
            if (Math.Abs(lo + hi - whole) <= 1e-10 * (right - left)) return lo + hi + (lo + hi - whole) / 15;
            if (depth == 24) throw new RequiredFactException("social memory cohort quadrature");
            return Part(left, mid, from, first, middle, lo, depth + 1) + Part(mid, right, middle, last, to, hi, depth + 1);
        }
    }

    private static double Fade(SocialMemoryDefRead memory, double age)
    {
        if (!(memory.DurationTicks > 0) || memory.FadeStarts is < 0 or > 1 || !double.IsFinite(memory.Offset)
            || !double.IsFinite(memory.Multiplier) || memory.Multiplier < 0)
            throw new RequiredFactException("social memory mechanics:" + memory.Def);
        if (age >= memory.DurationTicks) return 0;
        return memory.FadeStarts == 1 ? 1 : Math.Clamp((memory.DurationTicks - age) / (memory.DurationTicks * (1 - memory.FadeStarts)), 0, 1);
    }

    // Supported homogeneous mood groups have native average(offset) × sum(multiplier^rank), equal to
    // the global-rank sum here. Social groups use their own other pawn's youngest-first rank instead.
    // Native per-other removal precedes global removal. For another owner only its newest same-pawn
    // cap survives to compete for the global cap; counting every arrival would over-evict other owners.
    private static double Weight(SocialMemoryDefRead memory, string other, IReadOnlyDictionary<string, double> means,
        IReadOnlyDictionary<string, int> ranks, bool social)
    {
        var ownRank = ranks.GetValueOrDefault(other);
        var global = memory.StackLimit < 0 ? int.MaxValue : memory.StackLimit - 1;
        var same = memory.SamePawnLimit < 0 ? int.MaxValue : memory.SamePawnLimit - 1 - ownRank;
        if (global < 0 || same < 0) return 0;
        var own = means.GetValueOrDefault(other);
        if (global == int.MaxValue && same == int.MaxValue)
        {
            var total = social ? own : means.Values.Sum();
            var rank = social ? ownRank : ranks.Values.Sum();
            return Math.Pow(memory.Multiplier, rank) * Math.Exp(total * (memory.Multiplier - 1));
        }
        if (global == int.MaxValue) throw new RequiredFactException("unbounded global social memory:" + memory.Def);
        var others = new double[global + 1]; others[0] = 1;
        foreach (var id in means.Keys.Concat(ranks.Keys).Distinct(StringComparer.Ordinal).Where(id => id != other))
        {
            var rank = ranks.GetValueOrDefault(id);
            var cap = memory.SamePawnLimit < 0 ? global + 1 : memory.SamePawnLimit;
            var counts = PoissonCounts(means.GetValueOrDefault(id), Math.Min(global + 1, cap), true);
            var next = new double[global + 1];
            for (var prior = 0; prior <= global; prior++)
                for (var n = 0; n < counts.Length; n++)
                {
                    var retained = Math.Min(cap, rank + n);
                    if (prior + retained <= global) next[prior + retained] += others[prior] * counts[n];
                }
            others = next;
        }
        double result = 0;
        var ownCounts = PoissonCounts(own, Math.Min(global, same), false);
        for (var k = 0; k < ownCounts.Length; k++)
            for (var j = 0; j + k + ownRank <= global; j++)
                result += ownCounts[k] * others[j] * Math.Pow(memory.Multiplier, ownRank + k + (social ? 0 : j));
        return result;
    }
}
