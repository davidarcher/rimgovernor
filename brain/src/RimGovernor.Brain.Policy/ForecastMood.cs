using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record ForecastMoodPoint(double Tick, double Target, double Actual, bool Asleep);

/// <summary>
/// A pawn's mood over the horizon (#3897): <see cref="Projected"/> with its integrated target and actual mood, or
/// <see cref="Unprojected"/> when the timeline refuses its inputs (stacked changes, unmodeled conditional thoughts, an
/// unsupported renewal, the event budget), the findings naming why.
/// </summary>
public abstract record ForecastMoodResult(EquatableArray<string> Findings)
{
    /// <summary>The projected mood: the trajectory (empty when the search asked for integrals only), the target's ticks below the
    /// break threshold, the actual mood's ticks below it and their depth, the actual mood at the horizon, the target's pressure ticks and
    /// its break state.</summary>
    public sealed record Projected(EquatableArray<ForecastMoodPoint> Trajectory,
        double TargetExposureTicks, double ActualExposureTicks,
        double ActualDepthTicks, double TerminalMood, double PressureTicks, ForecastMoodBreaks Breaks,
        EquatableArray<string> Findings) : ForecastMoodResult(Findings);

    /// <summary>The timeline refused its inputs; <see cref="ForecastMoodResult.Findings"/> name why.</summary>
    public sealed record Unprojected(EquatableArray<string> Findings) : ForecastMoodResult(Findings);
}

/// <summary>
/// The target mood's mental breaks over the horizon: <see cref="Assessed"/> against the pawn's observed break threshold, or
/// <see cref="Unassessed"/> when the threshold is not a finite positive level.
/// </summary>
public abstract record ForecastMoodBreaks
{
    private protected ForecastMoodBreaks() { }

    /// <summary>Expected breaks (MoodBreakModel.BreaksPerDay summed over the target trajectory); the ticks the target spends in
    /// each break tier, mildest first, that the capability-days ledger prices by each tier's mean break days (#3520); and per
    /// break shift the timeline was given (#3655), the tier ticks of the target shifted by it (a housing move's mood delta
    /// over 100), clamped at 0 and 1 on its own.</summary>
    public sealed record Assessed(double Risk, EquatableArray<double> TierTicks, EquatableArray<ForecastShiftedBreaks> Shifted) : ForecastMoodBreaks;

    /// <summary>No finite positive break threshold was observed.</summary>
    public sealed record Unassessed : ForecastMoodBreaks;
}

/// <summary>The break tier ticks of a mood target shifted by <paramref name="Shift"/> (#3655).</summary>
public sealed record ForecastShiftedBreaks(double Shift, EquatableArray<double> TierTicks);

/// <summary>
/// A memory renewal at one tick (#3339), as ApplyBedThoughts or the weather check makes one: every memory of the ended
/// defs stops counting from At (an observed one and an earlier renewal's gain alike), then the gains start at At. A gain
/// is itself ended by the next later renewal that ends its def. Ends names every gained def.
/// </summary>
public sealed record ForecastMemoryRenewal(double At, EquatableArray<string> Ends, EquatableArray<NeedMemory> Gains)
{
    /// <summary>
    /// The chance the renewal happens (#3871): 1 for a certain one; less for a sleep inside the game's random bed-thought onset
    /// window (Toils_LayDown's Rand.Range(2500, 10000)). An uncertain renewal is priced at its expectation: its gains count
    /// <c>Chance</c> of their offset and every memory it ends keeps <c>1 - Chance</c> of its own.
    /// </summary>
    public double Chance { get; init; } = 1;
}

/// <summary>
/// The expected share of a memory that still counts at a tick (#3871): its base share, the chance it was gained, times
/// <c>Keep</c> of every cut at or before the tick. A certain renewal of its def cuts it to 0, an uncertain one keeps
/// <c>1 - Chance</c>.
/// </summary>
internal sealed class MemoryShare(double baseShare = 1)
{
    private readonly List<(double At, double Keep)> cuts = [];
    internal IReadOnlyList<(double At, double Keep)> Cuts => cuts;
    internal void Cut(double at, double keep) => cuts.Add((at, keep));
    internal double At(double state)
    {
        var share = baseShare;
        foreach (var (at, keep) in cuts) if (at <= state) share *= keep;
        return share;
    }
}

public static class ForecastMood
{
    /// <summary>
    /// The timed memories of a renewal list against the observed ones: each observed def's share (cut at each renewal that
    /// ends it) and each gain with its start and share (its renewal's chance, cut at each later renewal of its def). Null when
    /// a renewal is malformed (a gain its own Ends omits, a gain of unknown duration, a non-finite time, a chance outside
    /// (0, 1]) or a meal or granted memory shares a renewed def, which is not additive evidence.
    /// </summary>
    internal static (Dictionary<string, MemoryShare> Observed, List<(NeedMemory Thought, double Start, MemoryShare Share)> Gains)? RenewalSchedule(
        IReadOnlyList<NeedMemory> observed, IReadOnlyList<ForecastMemoryRenewal>? renewals, IEnumerable<string> mealIds)
    {
        var shares = observed.Select(m => m.Id).Distinct(StringComparer.Ordinal).ToDictionary(id => id, _ => new MemoryShare(), StringComparer.Ordinal);
        var gains = new List<(NeedMemory Thought, double Start, MemoryShare Share)>();
        if (renewals is null || renewals.Count == 0) return (shares, gains);
        var ordered = renewals.OrderBy(r => r.At).ToArray();
        if (ordered.Any(r => !double.IsFinite(r.At) || r.At < 0 || !(r.Chance > 0 && r.Chance <= 1) || r.Gains.Any(g => !r.Ends.Contains(g.Id)
            || !double.IsFinite(g.Offset))))
            return null;
        var renewed = ordered.SelectMany(r => r.Ends).ToHashSet(StringComparer.Ordinal);
        if (mealIds.Any(renewed.Contains)) return null;
        for (var i = 0; i < ordered.Length; i++)
        {
            var at = ordered[i].At;
            foreach (var id in ordered[i].Ends)
                if (shares.TryGetValue(id, out var share)) share.Cut(at, 1 - ordered[i].Chance);
            foreach (var gain in ordered[i].Gains)
            {
                var share = new MemoryShare(ordered[i].Chance);
                foreach (var later in ordered.Skip(i + 1).Where(r => r.At > at && r.Ends.Contains(gain.Id))) share.Cut(later.At, 1 - later.Chance);
                gains.Add((gain, at, share));
            }
        }
        return (shares, gains);
    }

    /// <summary>
    /// The tick each planned memory (a completed meal's thought or a granted one) stops counting, and the tick each observed
    /// def is ended at by a planned memory (#3598). A planned memory of a stackLimit-1 def renews the one live before it, as
    /// Thought_Memory.TryMergeWithExistingMemory does: that observed or earlier planned memory stops at its start. Null when a
    /// planned memory shares a def of any other stack limit, whose stacking is not additive evidence.
    /// </summary>
    internal static (double[] Ends, Dictionary<string, double> ObservedEnds)? PlannedMemoryEnds(
        IReadOnlyList<NeedMemory> observed, IReadOnlyList<(NeedMemory Thought, double Start)> planned)
    {
        var ends = new double[planned.Count];
        Array.Fill(ends, double.PositiveInfinity);
        var observedEnds = new Dictionary<string, double>(StringComparer.Ordinal);
        var order = Enumerable.Range(0, planned.Count).OrderBy(i => planned[i].Start).ToArray();
        for (var k = 0; k < order.Length; k++)
        {
            var (thought, start) = planned[order[k]];
            var earlier = order.Take(k).Where(j => planned[j].Thought.Id == thought.Id).ToArray();
            var old = observed.Any(m => m.Id == thought.Id);
            if (earlier.Length == 0 && !old) continue;
            if (thought.StackLimit != 1) return null;
            foreach (var j in earlier) ends[j] = Math.Min(ends[j], start);
            if (old && !observedEnds.ContainsKey(thought.Id)) observedEnds[thought.Id] = start;
        }
        return (ends, observedEnds);
    }

    /// <summary>Observed memories sharing a def (a stack, or social memories about different pawns) each expire on their own
    /// timer and are summed. Native values a stack as its mean offset times the stacked multipliers
    /// (ThoughtHandler.MoodOffsetOfGroup), so one member's expiry moves mood by less than its own offset: the finding names
    /// the approximation. Zero-offset memories (opinion-only social ones) do not count toward it.</summary>
    /// <summary>A memory's offset at an elapsed time, live or expired as at the state's elapsed time.</summary>
    internal static double MemoryAt(NeedMemory m, double elapsed, double state) =>
        m.Life is not MemoryLife.Expires { RemainingTicks: var r } ? m.Offset : state >= r ? 0 : m.Fades ? m.Offset * (1 - elapsed / r) : m.Offset;
    /// <summary>The ticks until a memory expires; null for a permanent one.</summary>
    internal static double? ExpiresIn(NeedMemory m) => m.Life is MemoryLife.Expires e ? e.RemainingTicks : null;

    internal static string? StackedMemoryFinding(IReadOnlyList<NeedMemory> memories) =>
        memories.Where(m => m.Offset != 0).GroupBy(m => m.Id, StringComparer.Ordinal).Any(g => g.Count() > 1)
            ? "stacked_memory_expiry_additive" : null;

    /// <summary>Conditional thoughts are native mechanics, independent of candidate family or trait names.
    /// Target and actual mood remain separate; threshold exposure is not itself a break probability. Granted
    /// memories (a commitment's MoodMemories) start at their tick like a completed meal's thoughts. A situational
    /// change adds its offset delta from its tick. A banded thought (#3327) adds its delta inside each of its spans and
    /// stays at the observed constant outside them, naming itself in a finding when they do not cover the horizon.
    /// Every other situational thought stays inside the mood's UnclampedTarget. Without trajectory the per-point mood is left
    /// empty: the search reads only the integrated results (#3467). The pawn is asleep only inside its rest services: one asleep
    /// now without a rest from tick 0 is woken at it (#3882), its observed sleep continuing only as such a rest.</summary>
    public static ForecastMoodResult ForecastMoodTimeline(NeedsForecastPawn pawn, double horizon,
        IReadOnlyList<ForecastNeedChoice> selected, IReadOnlyDictionary<string, double>? interruptedAt = null,
        IReadOnlyList<(NeedMemory Thought, double Start)>? granted = null,
        IReadOnlyList<(string Thought, double OffsetDelta, double Start)>? situational = null,
        IReadOnlyList<(string Thought, IReadOnlyList<(double Start, double End, double OffsetDelta)> Spans)>? banded = null,
        IReadOnlyList<ForecastMemoryRenewal>? renewals = null, bool trajectory = true, IReadOnlyList<double>? breakShifts = null)
    {
        ForecastNeeds.NeedHorizon(horizon);
        situational ??= [];
        banded ??= [];
        // Changing one thought twice is not additive evidence.
        var changed = situational.Select(s => s.Thought).Concat(banded.Select(b => b.Thought)).ToArray();
        if (changed.Distinct(StringComparer.Ordinal).Count() != changed.Length
            || situational.Any(s => !double.IsFinite(s.OffsetDelta)) || banded.Any(b => b.Spans.Any(s => !double.IsFinite(s.OffsetDelta))))
            return new ForecastMoodResult.Unprojected(["unsupported_situational_change_stacking"]);
        var unknown = new SortedSet<string>(StringComparer.Ordinal) { "unmodeled_situational_thoughts_constant" };
        foreach (var thought in banded.Where(b => b.Spans.Sum(s => s.End - s.Start) < horizon))
            unknown.Add("situational_thought_constant_outside_exposure:" + thought.Thought);
        var points = new List<ForecastMoodPoint>();
        var events = new SortedSet<double> { 0, horizon };
        var intervals = selected.Select(c => (Choice: c, End: interruptedAt is not null && interruptedAt.TryGetValue(c.Id, out var at)
            ? Math.Clamp(at, c.Depart, c.End) : c.End)).ToArray();
        var sleepEnds = ForecastNeeds.SleepServiceEnds(pawn, selected, interruptedAt, unknown);
        if (sleepEnds is null) return new ForecastMoodResult.Unprojected(EquatableArray.From(unknown));
        foreach (var end in sleepEnds.Values) events.Add(end);
        foreach (var row in intervals) { events.Add(row.Choice.Start); events.Add(row.End); }
        if (pawn.Mood is not { } mood) return new ForecastMoodResult.Unprojected(["unknown_conditional_mood_mechanics"]);
        // Each conditional def the native read cannot price names itself in the finding.
        if (mood.Conditional is not ConditionalMood.Modeled { Thoughts: var effects })
            return new ForecastMoodResult.Unprojected(EquatableArray.From(((ConditionalMood.Unmodeled)mood.Conditional).Thoughts
                .Select(t => "unknown_conditional_mood_mechanics:" + t).ToArray()));
        var memories = mood.Memories;
        if (StackedMemoryFinding(memories) is { } stacked) unknown.Add(stacked);
        foreach (var boundary in ConditionalThoughtTransitions(pawn, horizon)) events.Add(boundary);
        foreach (var memory in memories)
            if (ExpiresIn(memory) is { } expiry && expiry > 0 && expiry < horizon) events.Add(expiry);
        var meals = intervals.Where(r => r.Choice.Kind == "food" && r.End == r.Choice.End)
            .SelectMany(r => r.Choice.Thoughts.Select(t => (Thought: t, Start: r.End))).Concat(granted ?? []).ToArray();
        if (PlannedMemoryEnds(memories, meals) is not { } planned
            || RenewalSchedule(memories, renewals, meals.Select(m => m.Thought.Id)) is not { } schedule)
            return new ForecastMoodResult.Unprojected(["unsupported_memory_renewal_or_stacking"]);
        var (observedShares, renewed) = schedule;
        foreach (var (id, at) in planned.ObservedEnds) observedShares[id].Cut(at, 0);
        var mealEnds = planned.Ends;
        for (var i = 0; i < meals.Length; i++)
        {
            var meal = meals[i];
            if (ExpiresIn(meal.Thought) is { } mealExpiry && meal.Start + mealExpiry < horizon) events.Add(meal.Start + mealExpiry);
            if (mealEnds[i] < horizon) events.Add(mealEnds[i]);
        }
        foreach (var gain in renewed)
        {
            events.Add(gain.Start);
            foreach (var (at, _) in gain.Share.Cuts) if (at < horizon) events.Add(at);
            if (ExpiresIn(gain.Thought) is { } gainExpiry && gain.Start + gainExpiry < horizon) events.Add(gain.Start + gainExpiry);
        }
        foreach (var share in observedShares.Values)
            foreach (var (at, _) in share.Cuts) if (at > 0 && at < horizon) events.Add(at);
        foreach (var change in situational) if (change.Start > 0 && change.Start < horizon) events.Add(change.Start);
        foreach (var bound in banded.SelectMany(b => b.Spans).SelectMany(s => new[] { s.Start, s.End }))
            if (bound > 0 && bound < horizon) events.Add(bound);
        var actual = mood.Level;
        double targetExposure = 0, targetDepth = 0, actualExposure = 0, actualDepth = 0, pressure = 0;
        var threshold = mood.BreakThreshold;
        var breakKnown = QuestCapacityForecast.FinitePositive(threshold);
        if (!breakKnown) unknown.Add("mental_break_probability_unassessed");
        // The observed threshold is the pawn's minor-break level, the model's base for every tier.
        var breakModel = new MoodBreakModel { Threshold = threshold };
        Span<double> tierTicks = stackalloc double[LayoutMoodRules.MoodTierCount];
        var shifts = breakKnown && breakShifts is { Count: > 0 } ? breakShifts : [];
        var shiftedTicks = shifts.Count == 0 ? [] : new double[shifts.Count * LayoutMoodRules.MoodTierCount];
        // Actual mood chases the target continuously, in exact segments (#3468); native's discrete updates, whose phase
        // is not observed, are the held approximation.
        if (events.Count > 4096) return new ForecastMoodResult.Unprojected(["mood_event_budget_truncated"]);
        var ticks = events.Where(t => t >= 0 && t <= horizon).ToArray();
        bool Asleep(double time)
        {
            foreach (var row in intervals)
                if (row.Choice.Kind == "rest" && row.Choice.Start <= time && time < sleepEnds[row.Choice.Id]) return true;
            return false;
        }
        var currentConditionalOffset = effects.Sum(e => e.CurrentOffset);
        var bandedSpans = banded.SelectMany(b => b.Spans).ToArray();
        double Target(double time, bool asleep) => Math.Clamp(Unclamped(time, asleep, time), 0, 1);
        // The target's linear terms at a time, with every on/off state (hour bands, starts, ends, expiries) as at the
        // state time: inside a segment that is its midpoint, so a rounded boundary tick cannot flip the segment.
        double Unclamped(double time, bool asleep, double state)
        {
            var hour = (pawn.LocalHour + state / GameTime.TicksPerHour) % 24;
            double conditional = 0, changedMemories = 0, mealMemories = 0;
            foreach (var effect in effects)
                conditional += (!effect.AwakeOnly || !asleep) && InHours(hour, effect.StartHour, effect.EndHour) ? effect.Offset : 0;
            foreach (var memory in memories) changedMemories += MemoryAt(memory, time, state) * observedShares[memory.Id].At(state) - memory.Offset;
            for (var i = 0; i < meals.Length; i++)
                if (meals[i].Start <= state && state < mealEnds[i]) mealMemories += MemoryAt(meals[i].Thought, time - meals[i].Start, state - meals[i].Start);
            foreach (var gain in renewed)
                if (gain.Start <= state) mealMemories += MemoryAt(gain.Thought, time - gain.Start, state - gain.Start) * gain.Share.At(state);
            changedMemories += mealMemories;
            foreach (var change in situational) if (change.Start <= state) changedMemories += change.OffsetDelta;
            foreach (var span in bandedSpans) if (span.Start <= state && state < span.End) changedMemories += span.OffsetDelta;
            return mood.UnclampedTarget + (conditional - currentConditionalOffset + changedMemories) / 100;
        }
        // Actual mood over a piece whose target moves linearly from one value to another: it moves at the rise or fall
        // rate toward the target, then follows it while the target moves no faster than that rate.
        void Chase(double from, double to, double duration, bool frozen)
        {
            if (frozen)
            {
                Integrate(threshold - actual, threshold - actual, duration, ref actualExposure, ref actualDepth);
                return;
            }
            var (rise, fall, slope) = (mood.RisePerTick, mood.FallPerTick, (to - from) / duration);
            double elapsed = 0;
            // At most an approach and then a follow; the last step takes whatever remains.
            for (var step = 0; elapsed < duration; step++)
            {
                var remaining = duration - elapsed;
                var gap = from + slope * elapsed - actual;
                var piece = remaining;
                double next;
                if (gap == 0 && slope >= -fall && slope <= rise) next = to;
                else
                {
                    var rate = gap > 0 || gap == 0 && slope > rise ? rise : -fall;
                    var meet = gap / (rate - slope);
                    if (step < 2 && gap != 0 && meet > 0 && meet < remaining) { piece = meet; next = from + slope * (elapsed + meet); }
                    else next = actual + rate * remaining;
                }
                Integrate(threshold - actual, threshold - next, piece, ref actualExposure, ref actualDepth);
                actual = next; elapsed += piece;
            }
        }
        Span<double> cuts = stackalloc double[4];
        for (var i = 0; i < ticks.Length; i++)
        {
            var from = ticks[i]; var asleep = Asleep(from); var target = Target(from, asleep);
            if (trajectory) points.Add(new(from, target, actual, asleep));
            if (i == ticks.Length - 1) break;
            var duration = ticks[i + 1] - from;
            // The target is linear between events except where the clamp cuts it at 0 or 1.
            var middle = from + duration / 2;
            asleep = Asleep(middle);
            var (rawFrom, rawTo) = (Unclamped(from, asleep, middle), Unclamped(ticks[i + 1], asleep, middle));
            var count = 0;
            cuts[count++] = 0;
            if (rawFrom != rawTo)
                foreach (var bound in (ReadOnlySpan<double>)[0.0, 1.0])
                    if ((bound - rawFrom) / (rawTo - rawFrom) is var cut and > 0 and < 1) cuts[count++] = cut;
            cuts[count++] = 1;
            cuts[..count].Sort();
            for (var c = 0; c < count - 1; c++)
            {
                var a = c == 0 ? Math.Clamp(rawFrom, 0, 1) : Math.Clamp(rawFrom + (rawTo - rawFrom) * cuts[c], 0, 1);
                var b = c == count - 2 ? Math.Clamp(rawTo, 0, 1) : Math.Clamp(rawFrom + (rawTo - rawFrom) * cuts[c + 1], 0, 1);
                var piece = duration * (cuts[c + 1] - cuts[c]);
                if (piece <= 0) continue;
                pressure += (1 - (a + b) / 2) * piece;
                Integrate(threshold - a, threshold - b, piece, ref targetExposure, ref targetDepth); // the target's depth is not read
                if (breakKnown) breakModel.AddTierTicks(a, b, piece, tierTicks);
                Chase(a, b, piece, asleep && mood.FreezesWhileAsleep);
            }
            // Each shifted target (#3655) has its own clamp cuts.
            for (var k = 0; k < shifts.Count; k++)
            {
                var (sFrom, sTo) = (rawFrom + shifts[k], rawTo + shifts[k]);
                count = 0;
                cuts[count++] = 0;
                if (sFrom != sTo)
                    foreach (var bound in (ReadOnlySpan<double>)[0.0, 1.0])
                        if ((bound - sFrom) / (sTo - sFrom) is var cut and > 0 and < 1) cuts[count++] = cut;
                cuts[count++] = 1;
                cuts[..count].Sort();
                for (var c = 0; c < count - 1; c++)
                {
                    var piece = duration * (cuts[c + 1] - cuts[c]);
                    if (piece <= 0) continue;
                    breakModel.AddTierTicks(Math.Clamp(sFrom + (sTo - sFrom) * cuts[c], 0, 1), Math.Clamp(sFrom + (sTo - sFrom) * cuts[c + 1], 0, 1), piece, shiftedTicks.AsSpan(k * LayoutMoodRules.MoodTierCount, LayoutMoodRules.MoodTierCount));
                }
            }
        }
        unknown.Add("mood_response_interval_phase_unassessed");
        ForecastMoodBreaks breaks = breakKnown
            ? new ForecastMoodBreaks.Assessed(MoodBreakModel.Breaks(tierTicks), EquatableArray.From(tierTicks.ToArray()),
                EquatableArray.From(shifts.Select((shift, k) => new ForecastShiftedBreaks(shift,
                    EquatableArray.From(shiftedTicks.AsSpan(k * LayoutMoodRules.MoodTierCount, LayoutMoodRules.MoodTierCount).ToArray())))))
            : new ForecastMoodBreaks.Unassessed();
        return new ForecastMoodResult.Projected(EquatableArray.From(points), targetExposure, actualExposure, actualDepth, actual,
            pressure, breaks, EquatableArray.From(unknown));
    }

    // The need thoughts' baseMoodEffect by stage (Core ThoughtDefs/Thoughts_Situation_Needs.xml, NeedFood and NeedRest): no
    // worker overrides ThoughtWorker.MoodMultiplier and neither def has an effect-multiplying stat, so Thought.MoodOffset is
    // the base effect unless a trait, hediff, precept or gene nullifies the thought, which is not observed. Food's stage 2 is
    // the first malnourished stage; the deeper ones (-26 to -44) follow the malnutrition hediff and are not priced.
    private static readonly double[] NeedFoodMood = [-6, -12, -20];
    private static readonly double[] NeedRestMood = [-6, -12, -18];

    /// <summary>
    /// The food and rest thoughts along the needs timeline (#3598), as banded rows covering the horizon: each stage span adds
    /// the stage's mood less the observed stage's, which <see cref="NeedMood.UnclampedTarget"/> already holds.
    /// The thoughts are named by def (Policy cannot reference ThoughtDefOf). A need with no stage spans stays constant.
    /// </summary>
    internal static IEnumerable<(string Thought, IReadOnlyList<(double Start, double End, double OffsetDelta)> Spans)> NeedThoughts(IEnumerable<ForecastNeedMetric> needs)
    {
        foreach (var need in needs)
        {
            if (need.StageSpans.Count == 0) continue;
            var (thought, moods) = need.Kind switch { "food" => ("NeedFood", NeedFoodMood), "rest" => ("NeedRest", NeedRestMood), _ => ("", []) };
            if (moods.Length == 0) continue;
            var observed = need.StageSpans[0].Stage < 0 ? 0 : moods[need.StageSpans[0].Stage];
            var spans = new (double Start, double End, double OffsetDelta)[need.StageSpans.Count];
            for (var i = 0; i < spans.Length; i++)
            {
                var span = need.StageSpans[i];
                spans[i] = (span.Start, span.End, (span.Stage < 0 ? 0 : moods[span.Stage]) - observed);
            }
            yield return (thought, spans);
        }
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
    /// spans (the observed constant stays) for a pawn without card facts or when any piece's card is unmodeled.
    /// </summary>
    internal static (string Thought, IReadOnlyList<(double Start, double End, double OffsetDelta)> Spans) TemperatureThought(MechanicCard card,
        CardPawnFacts? pawn, EquatableArray<MoodThought> observed, IReadOnlyList<ForecastThermalSpan> exposure)
    {
        var none = (card.Def, (IReadOnlyList<(double, double, double)>)[]);
        if (pawn is null) return none;
        var current = observed.Where(t => t.Def == card.Def).Sum(t => t.Offset);
        var cuts = MechanicCardGuards.StageCuts(card);
        var rows = new List<(double Start, double End, double OffsetDelta)>();
        foreach (var span in exposure)
        {
            CardStageFacts At(double tick) => new(span.ComfortableMin, span.ComfortableMax,
                span.Initial + (span.Final - span.Initial) * (tick - span.Start) / (span.End - span.Start));
            var times = new SortedSet<double> { span.Start, span.End };
            foreach (var site in card.Sites)
            {
                if (!MechanicCardGuards.TryStageInput(site, pawn, At(span.Start), out var from)
                    || !MechanicCardGuards.TryStageInput(site, pawn, At(span.End), out var to)) return none;
                if (from == to) continue;
                foreach (var cut in cuts)
                    if ((cut - from) / (to - from) is var f and > 0 and < 1) times.Add(span.Start + f * (span.End - span.Start));
            }
            var points = times.ToArray();
            for (var i = 0; i < points.Length - 1; i++)
            {
                if (MechanicCardGuards.CardStageOffset(card, pawn, At((points[i] + points[i + 1]) / 2)) is not CardOffset.Staged { Offset: var offset }) return none;
                rows.Add((points[i], points[i + 1], offset - current));
            }
        }
        return (card.Def, rows);
    }

    internal static IEnumerable<double> ConditionalThoughtTransitions(NeedsForecastPawn pawn, double horizon)
    {
        if (pawn.Mood is not { Conditional: ConditionalMood.Modeled { Thoughts: var effects } }) yield break;
        var hour = pawn.LocalHour;
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
