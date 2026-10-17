using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record ForecastMoodPoint(double Tick, Fact<double> Target, Fact<double> Actual, bool Asleep);
public sealed record ForecastMoodResult(EquatableArray<ForecastMoodPoint> Trajectory,
    Fact<double> TargetExposureTicks, Fact<double> TargetDepthTicks, Fact<double> ActualExposureTicks,
    Fact<double> ActualDepthTicks, Fact<double> TerminalMood, EquatableArray<string> Findings)
{
    public Fact<double> PressureTicks { get; init; }
    /// <summary>Expected mental breaks over the horizon from the target trajectory (MoodBreakModel.BreaksPerDay); unknown without an observed threshold.</summary>
    public Fact<double> BreakRisk { get; init; }
}

/// <summary>
/// A memory renewal at one tick (#3339), as ApplyBedThoughts or the weather check makes one: every memory of the ended
/// defs stops counting from At (an observed one and an earlier renewal's gain alike), then the gains start at At. A gain
/// is itself ended by the next later renewal that ends its def. Ends names every gained def.
/// </summary>
public sealed record ForecastMemoryRenewal(double At, EquatableArray<string> Ends, EquatableArray<NeedMemory> Gains);

public static class ForecastMood
{
    /// <summary>
    /// The timed memories of a renewal list against the observed ones: the tick each observed memory is ended at
    /// (infinity when no renewal ends it) and each gain with its start and end. Null when a renewal is malformed (a
    /// gain its own Ends omits, a gain of unknown duration, a non-finite time) or a meal or granted memory shares a
    /// renewed def, which is not additive evidence.
    /// </summary>
    internal static (Dictionary<string, double> ObservedEnds, List<(NeedMemory Thought, double Start, double End)> Gains)? RenewalSchedule(
        IReadOnlyList<NeedMemory> observed, IReadOnlyList<ForecastMemoryRenewal>? renewals, IEnumerable<string> mealIds)
    {
        var ends = observed.ToDictionary(m => m.Id, _ => double.PositiveInfinity, StringComparer.Ordinal);
        var gains = new List<(NeedMemory Thought, double Start, double End)>();
        if (renewals is null || renewals.Count == 0) return (ends, gains);
        var ordered = renewals.OrderBy(r => r.At).ToArray();
        if (ordered.Any(r => !double.IsFinite(r.At) || r.At < 0 || r.Gains.Any(g => !r.Ends.Contains(g.Id)
            || !g.Permanent && !g.RemainingTicks.IsKnown || !double.IsFinite(g.Offset))))
            return null;
        var renewed = ordered.SelectMany(r => r.Ends).ToHashSet(StringComparer.Ordinal);
        if (mealIds.Any(renewed.Contains)) return null;
        for (var i = 0; i < ordered.Length; i++)
        {
            var at = ordered[i].At;
            foreach (var id in ordered[i].Ends)
                if (ends.TryGetValue(id, out var end) && end > at) ends[id] = at;
            foreach (var gain in ordered[i].Gains)
            {
                var stop = ordered.Skip(i + 1).FirstOrDefault(r => r.At > at && r.Ends.Contains(gain.Id))?.At ?? double.PositiveInfinity;
                gains.Add((gain, at, stop));
            }
        }
        return (ends, gains);
    }

    /// <summary>Conditional thoughts are native mechanics, independent of candidate family or trait names.
    /// Target and actual mood remain separate; threshold exposure is not itself a break probability. Granted
    /// memories (a commitment's MoodMemories) start at their tick like a completed meal's thoughts. A situational
    /// change adds its offset delta from its tick. A banded thought (#3327) adds its delta inside each of its spans and
    /// stays at the observed constant outside them, naming itself in a finding when they do not cover the horizon.
    /// Every other situational thought stays inside MoodUnclampedTarget. Without trajectory the per-point mood is left
    /// empty: the search reads only the integrated results (#3467).</summary>
    public static ForecastMoodResult ForecastMoodTimeline(NeedsForecastPawn pawn, double horizon,
        IReadOnlyList<ForecastNeedChoice> selected, IReadOnlyDictionary<string, double>? interruptedAt = null,
        IReadOnlyList<(NeedMemory Thought, double Start)>? granted = null,
        IReadOnlyList<(string Thought, double OffsetDelta, double Start)>? situational = null,
        IReadOnlyList<(string Thought, IReadOnlyList<(double Start, double End, double OffsetDelta)> Spans)>? banded = null,
        IReadOnlyList<ForecastMemoryRenewal>? renewals = null, bool trajectory = true)
    {
        ForecastNeeds.NeedHorizon(horizon);
        situational ??= [];
        banded ??= [];
        // Changing one thought twice is not additive evidence.
        var changed = situational.Select(s => s.Thought).Concat(banded.Select(b => b.Thought)).ToArray();
        if (changed.Distinct(StringComparer.Ordinal).Count() != changed.Length
            || situational.Any(s => !double.IsFinite(s.OffsetDelta)) || banded.Any(b => b.Spans.Any(s => !double.IsFinite(s.OffsetDelta))))
            return new([], default, default, default, default, default, ["unsupported_situational_change_stacking"]);
        var unknown = new SortedSet<string>(StringComparer.Ordinal) { "unmodeled_situational_thoughts_constant" };
        foreach (var thought in banded.Where(b => b.Spans.Sum(s => s.End - s.Start) < horizon))
            unknown.Add("situational_thought_constant_outside_exposure:" + thought.Thought);
        var points = new List<ForecastMoodPoint>();
        var events = new SortedSet<double> { 0, horizon };
        var intervals = selected.Select(c => (Choice: c, End: interruptedAt is not null && interruptedAt.TryGetValue(c.Id, out var at)
            ? Math.Clamp(at, c.Depart, c.End) : c.End)).ToArray();
        if (pawn.Awake != Known(true) && !intervals.Any(r => r.Choice.Kind == "rest" && r.Choice.Start == 0))
            return new([], default, default, default, default, default, ["observed_sleep_continuation_unassessed"]);
        var sleepEnds = ForecastNeeds.SleepServiceEnds(pawn, selected, interruptedAt, unknown);
        if (sleepEnds is null) return new([], default, default, default, default, default, EquatableArray.From(unknown));
        foreach (var end in sleepEnds.Values) events.Add(end);
        foreach (var row in intervals) { events.Add(row.Choice.Start); events.Add(row.End); }
        var supported = pawn.ConditionalThoughts.TryGetValue(out var effects) && pawn.LocalHour.IsKnown
            && pawn.MoodUnclampedTarget.IsKnown && pawn.Memories.TryGetValue(out var memories);
        memories = pawn.Memories.ValueOrDefault;
        if (!supported || memories.Any(m => !m.Permanent && !m.RemainingTicks.IsKnown)
            || memories.Select(m => m.Id).Distinct().Count() != memories.Count)
            return new([], default, default, default, default, default, ["unknown_conditional_mood_mechanics"]);
        foreach (var boundary in ConditionalThoughtTransitions(pawn, horizon)) events.Add(boundary);
        foreach (var memory in memories.Where(m => !m.Permanent && m.RemainingTicks.IsKnown))
            if (memory.RemainingTicks.Value > 0 && memory.RemainingTicks.Value < horizon) events.Add(memory.RemainingTicks.Value);
        var meals = intervals.Where(r => r.Choice.Kind == "food" && r.End == r.Choice.End)
            .SelectMany(r => r.Choice.Thoughts.Select(t => (Thought: t, Start: r.End))).Concat(granted ?? []).ToArray();
        if (meals.Select(m => m.Thought.Id).Distinct().Count() != meals.Length
            || meals.Any(m => memories.Any(old => old.Id == m.Thought.Id) || !m.Thought.Permanent && !m.Thought.RemainingTicks.IsKnown))
            return new([], default, default, default, default, default, ["unsupported_memory_renewal_or_stacking"]);
        if (RenewalSchedule(memories, renewals, meals.Select(m => m.Thought.Id)) is not { } schedule)
            return new([], default, default, default, default, default, ["unsupported_memory_renewal_or_stacking"]);
        var (observedEnds, renewed) = schedule;
        foreach (var meal in meals)
            if (!meal.Thought.Permanent && meal.Start + meal.Thought.RemainingTicks.Value < horizon)
                events.Add(meal.Start + meal.Thought.RemainingTicks.Value);
        foreach (var gain in renewed)
        {
            events.Add(gain.Start);
            if (gain.End < horizon) events.Add(gain.End);
            if (!gain.Thought.Permanent && gain.Start + gain.Thought.RemainingTicks.Value < horizon)
                events.Add(gain.Start + gain.Thought.RemainingTicks.Value);
        }
        foreach (var end in observedEnds.Values) if (end > 0 && end < horizon) events.Add(end);
        foreach (var change in situational) if (change.Start > 0 && change.Start < horizon) events.Add(change.Start);
        foreach (var bound in banded.SelectMany(b => b.Spans).SelectMany(s => new[] { s.Start, s.End }))
            if (bound > 0 && bound < horizon) events.Add(bound);
        var actualKnown = pawn.Mood.IsKnown && pawn.MoodRisePerTick.IsKnown && pawn.MoodFallPerTick.IsKnown
            && pawn.MoodFreezesWhileAsleep.IsKnown;
        var actual = pawn.Mood.ValueOrDefault;
        double targetExposure = 0, targetDepth = 0, actualExposure = 0, actualDepth = 0, pressure = 0;
        var thresholdKnown = pawn.BreakThreshold.TryGetValue(out var threshold);
        var breakKnown = thresholdKnown && QuestCapacityForecast.FinitePositive(threshold);
        if (!breakKnown) unknown.Add("mental_break_probability_unassessed");
        // The observed threshold is the pawn's minor-break level, the model's base for every tier.
        var breakModel = new MoodBreakModel { Threshold = threshold };
        Span<double> tierTicks = stackalloc double[LayoutMoodRules.MoodTierCount];
        // Bound response interpolation by native update-sized intervals. Phase is not observed.
        if (pawn.MoodIntervalTicks.TryGetValue(out var interval) && double.IsFinite(interval) && interval > 0)
        {
            if (horizon / interval > 4094) return new([], default, default, default, default, default, ["mood_event_budget_truncated"]);
            for (var tick = interval; tick < horizon; tick += interval) events.Add(tick);
        }
        else { actualKnown = false; unknown.Add("unknown_mood_response_interval"); }
        if (events.Count > 4096) return new([], default, default, default, default, default, ["mood_event_budget_truncated"]);
        var ticks = events.Where(t => t >= 0 && t <= horizon).ToArray();
        double Memory(NeedMemory m, double elapsed) => m.Permanent ? m.Offset : elapsed >= m.RemainingTicks.Value ? 0
            : m.Fades ? m.Offset * (1 - elapsed / m.RemainingTicks.Value) : m.Offset;
        bool Asleep(double time)
        {
            foreach (var row in intervals)
                if (row.Choice.Kind == "rest" && row.Choice.Start <= time && time < sleepEnds[row.Choice.Id]) return true;
            return false;
        }
        var currentConditionalOffset = effects.Sum(e => e.CurrentOffset);
        var bandedSpans = banded.SelectMany(b => b.Spans).ToArray();
        double Target(double time, bool asleep)
        {
            var hour = (pawn.LocalHour.Value + time / GameTime.TicksPerHour) % 24;
            double conditional = 0, changedMemories = 0, mealMemories = 0;
            foreach (var effect in effects)
                conditional += (!effect.AwakeOnly || !asleep) && InHours(hour, effect.StartHour, effect.EndHour) ? effect.Offset : 0;
            foreach (var memory in memories) changedMemories += (time < observedEnds[memory.Id] ? Memory(memory, time) : 0) - memory.Offset;
            foreach (var meal in meals)
                if (meal.Start <= time) mealMemories += Memory(meal.Thought, time - meal.Start);
            foreach (var gain in renewed)
                if (gain.Start <= time && time < gain.End) mealMemories += Memory(gain.Thought, time - gain.Start);
            changedMemories += mealMemories;
            foreach (var change in situational) if (change.Start <= time) changedMemories += change.OffsetDelta;
            foreach (var span in bandedSpans) if (span.Start <= time && time < span.End) changedMemories += span.OffsetDelta;
            return Math.Clamp(pawn.MoodUnclampedTarget.Value + (conditional - currentConditionalOffset + changedMemories) / 100, 0, 1);
        }
        for (var i = 0; i < ticks.Length; i++)
        {
            var from = ticks[i]; var asleep = Asleep(from); var target = Target(from, asleep);
            if (trajectory) points.Add(new(from, Known(target), actualKnown ? Known(actual) : default, asleep));
            if (i == ticks.Length - 1) break;
            var duration = ticks[i + 1] - from;
            var endTarget = Target(Math.BitDecrement(ticks[i + 1]), asleep);
            pressure += (1 - (target + endTarget) / 2) * duration;
            if (thresholdKnown) Integrate(threshold - target, threshold - endTarget, duration, ref targetExposure, ref targetDepth);
            if (breakKnown) breakModel.AddTierTicks(target, endTarget, duration, tierTicks);
            if (actualKnown)
            {
                var previous = actual;
                if (!asleep || !pawn.MoodFreezesWhileAsleep.Value)
                {
                    var rate = target >= actual ? pawn.MoodRisePerTick.Value : -pawn.MoodFallPerTick.Value;
                    var movement = rate * duration;
                    actual = rate >= 0 ? Math.Min(target, actual + movement) : Math.Max(target, actual + movement);
                }
                if (thresholdKnown) Integrate(threshold - previous, threshold - actual, duration, ref actualExposure, ref actualDepth);
            }
        }
        unknown.Add("mood_response_interval_phase_unassessed");
        return new(EquatableArray.From(points), thresholdKnown ? Known(targetExposure) : default,
            thresholdKnown ? Known(targetDepth) : default, actualKnown && thresholdKnown ? Known(actualExposure) : default,
            actualKnown && thresholdKnown ? Known(actualDepth) : default, actualKnown ? Known(actual) : default, EquatableArray.From(unknown))
        { PressureTicks = Known(pressure), BreakRisk = breakKnown ? Known(MoodBreakModel.Breaks(tierTicks)) : default };
    }

    /// <summary>Whether a card prices temperature mood (#3327): Live, and every site's stage input reads the ambient temperature.</summary>
    internal static bool TemperatureCard(MechanicCard card) => card.Live && card.Sites.Count > 0
        && card.Sites.All(s => s.Stage is { } band && Reads(band.Input, "Thing.AmbientTemperature"));

    /// <summary>Whether a Live temperature card's stage input reads the comfort bound <paramref name="decl"/>, so a comfort shift on it moves its offset (#3329).</summary>
    internal static bool TemperatureCardReads(MechanicCard card, string decl) => TemperatureCard(card) && card.Sites.All(s => Reads(s.Stage!.Input, decl));

    private static bool Reads(MechanicCardExpr e, string decl) => e.Decl == decl || (e.Args ?? []).Any(a => Reads(a, decl));

    /// <summary>
    /// A temperature card's offset along a pawn's assessed exposure spans (#3327), as deltas from the thought's observed
    /// contribution (its <c>MoodPawn.Thoughts</c> rows; none is 0). Each span is cut where the stage input, linear in the
    /// span's temperature, crosses a literal the card compares against, and each piece is evaluated at its midpoint. No
    /// spans (the observed constant stays) when the facts, the observed thoughts or any piece's offset are unknown.
    /// </summary>
    internal static (string Thought, IReadOnlyList<(double Start, double End, double OffsetDelta)> Spans) TemperatureThought(MechanicCard card,
        CardPawnFacts? pawn, Fact<EquatableArray<MoodThought>> observed, IReadOnlyList<ForecastThermalSpan> exposure)
    {
        var none = (card.Def, (IReadOnlyList<(double, double, double)>)[]);
        if (pawn is null || !observed.TryGetValue(out var thoughts)) return none;
        var current = thoughts.Where(t => t.Def == card.Def).Sum(t => t.Offset);
        var cuts = MechanicCardGuards.StageCuts(card).ToArray();
        var rows = new List<(double Start, double End, double OffsetDelta)>();
        foreach (var span in exposure)
        {
            CardStageFacts At(double tick) => new(span.ComfortableMin, span.ComfortableMax,
                span.Initial + (span.Final - span.Initial) * (tick - span.Start) / (span.End - span.Start));
            var times = new SortedSet<double> { span.Start, span.End };
            foreach (var site in card.Sites)
            {
                if (!MechanicCardGuards.StageInput(site, pawn, At(span.Start)).TryGetValue(out var from)
                    || !MechanicCardGuards.StageInput(site, pawn, At(span.End)).TryGetValue(out var to)) return none;
                if (from == to) continue;
                foreach (var cut in cuts)
                    if ((cut - from) / (to - from) is var f and > 0 and < 1) times.Add(span.Start + f * (span.End - span.Start));
            }
            var points = times.ToArray();
            for (var i = 0; i < points.Length - 1; i++)
            {
                if (!MechanicCardGuards.CardStageOffset(card, pawn, At((points[i] + points[i + 1]) / 2)).TryGetValue(out var offset)) return none;
                rows.Add((points[i], points[i + 1], offset - current));
            }
        }
        return (card.Def, rows);
    }

    internal static IEnumerable<double> ConditionalThoughtTransitions(NeedsForecastPawn pawn, double horizon)
    {
        if (!pawn.ConditionalThoughts.TryGetValue(out var effects) || !pawn.LocalHour.TryGetValue(out var hour)) yield break;
        foreach (var effect in effects)
            foreach (var boundary in new[] { effect.StartHour, effect.EndHour })
            {
                var first = ((boundary - hour + 24) % 24) * GameTime.TicksPerHour;
                for (var tick = first; tick <= horizon; tick += GameTime.TicksPerDay) yield return tick;
            }
    }
    private static bool InHours(double hour, double start, double end) => start < end ? hour >= start && hour < end : hour >= start || hour < end;
    private static void Integrate(double a, double b, double duration, ref double exposure, ref double depth)
    {
        if (a <= 0 && b <= 0) return;
        if (a >= 0 && b >= 0) { exposure += duration; depth += (a + b) * duration / 2; return; }
        var positive = Math.Max(a, b); var fraction = positive / Math.Abs(a - b);
        exposure += duration * fraction; depth += positive * duration * fraction / 2;
    }
}
