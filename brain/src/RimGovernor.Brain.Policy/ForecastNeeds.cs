using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record ForecastNeedChoice(string Id, string Pawn, string Kind, string Target, string Resource,
    int Count, double Depart, double Start, double End, double Gain, EquatableArray<NeedMemory> Thoughts)
{
    public ForecastOccupancy Occupancy => new(Id, Pawn, Depart, End, ForecastOccupancyState.Active);
    public ForecastActivity Activity => new(Id, Target, Target, [])
    { Inputs = Kind == "food" ? [new(new(Resource), Count)] : [] };
}
public sealed record ForecastNeedMetric(string Kind, ForecastTerm FinalLevel, ForecastTerm DeficitTicks,
    ForecastTerm ThresholdExposureTicks, ForecastTerm WastedGain)
{
    public ForecastTerm ZeroTicks { get; init; } = new ForecastTerm.Unassessed("need_unprojected");
    public ForecastTerm ThresholdDepthTicks { get; init; } = new ForecastTerm.Unassessed("need_unprojected");
    /// <summary>The spans, in ticks from the review, the need's level is zero (food: the game's Starving category, whose malnutrition
    /// the capability-days ledger prices, #3520). Empty when the need never reaches zero.</summary>
    public EquatableArray<ForecastNeedSpan> ZeroSpans { get; init; }
    /// <summary>Food and rest (#3598): the need thought's stage over the horizon, contiguous from the review to its end, the
    /// first span's stage being the observed one (<see cref="ForecastNeeds.NeedThoughtStage"/>). Empty for other needs and when
    /// the timeline is unknown. The mood timeline prices it (<see cref="ForecastMood.NeedThoughts"/>).</summary>
    public EquatableArray<ForecastNeedStageSpan> StageSpans { get; init; }
}
public readonly record struct ForecastNeedSpan(double Start, double End);
/// <summary>A span the need thought holds one stage; -1 is no thought (fed, rested).</summary>
public readonly record struct ForecastNeedStageSpan(double Start, double End, int Stage);
public sealed record ForecastNeedsResult(EquatableArray<ForecastNeedMetric> Needs,
    ForecastTerm ThoughtOffsetAtHorizon, ForecastTerm MemoryAdjustedMoodTarget,
    ForecastCapacityResult Capacity, EquatableArray<string> Findings)
{
    public ForecastMoodResult? Mood { get; init; }
    public IReadOnlyDictionary<string, double> UsefulFoodGain { get; init; } = new Dictionary<string, double>();
}
public sealed record ForecastNeedsCandidates(EquatableArray<ForecastNeedChoice> Choices,
    EquatableArray<string> Findings, bool Truncated);
public sealed record ForecastNeedsReview(NeedsForecastRead Observation,
    IReadOnlyDictionary<string, ForecastNeedsCandidates> Alternatives);

public static class ForecastNeeds
{
    /// <summary>Ordinary review exposes all generated choices for coupled comparison, not a shadow winner.</summary>
    public static ForecastNeedsReview? ReviewNeedsAlternatives(NeedsForecastRead? observed, WorkResourceView? resources,
        double horizon = GameTime.TicksPerDay)
    {
        if (observed is null || resources is null) return null;
        return new(observed, observed.Pawns.ToDictionary(p => p.Pawn, p => GenerateNeedsAlternatives(p,
            NeedsWorkCapacity(resources, p.Pawn, horizon), horizon), StringComparer.Ordinal));
    }

    /// <summary>Native timetable is descriptive here. Only actual current work and journal ownership protect time.
    /// Explicit player obligations can be supplied to ForecastNeedsTimeline; legacy timetable writers are retired by #3135.</summary>
    public static ForecastCapacityResult NeedsWorkCapacity(WorkResourceView view, string pawn, double horizon)
        => WorkRules.WorkCapacityOf(view, pawn, horizon, protectTimetable: false);

    /// <summary>
    /// Whether the pawn's time is the brain's to select: eligible, or eligible but for a voluntary-control blocker
    /// (<c>unsupported_voluntary_state</c>). A blocker is a visible finding, not a capacity exclusion: the pawn's
    /// supported needs and labor stay selectable, so it is fed rather than left idle (#3557).
    /// </summary>
    public static bool Voluntary(string state) => state is "eligible" or "unsupported_voluntary_state";

    public static ForecastNeedsCandidates GenerateNeedsAlternatives(NeedsForecastPawn pawn, ForecastCapacityResult capacity,
        double horizon, int budget = 256)
    {
        NeedHorizon(horizon);
        if (budget < 1 || budget > 4096) throw new DomainException("invalid needs generation budget");
        var choices = new List<ForecastNeedChoice>();
        var choiceKeys = new HashSet<(string Target, string Kind, int Count, double Depart, double Start, double End)>();
        var findings = new SortedSet<string>(pawn.Unsupported, StringComparer.Ordinal);
        var truncated = pawn.Targets.Count > 256;
        if (!Voluntary(pawn.State)) return new([], ["needs_state:" + pawn.State], truncated);
        if (capacity.Capacity.Windows is not ForecastCapacityWindows.Read(var windows)) return new([], ["unknown_needs_capacity"], truncated);
        var plans = new List<(NeedTarget Target, double Level, double Fall, double Travel, List<(double Depart, (int Count, double Duration, double Gain) Variant)> Pairs)>();
        foreach (var target in pawn.Targets.Take(256).OrderBy(t => t.Kind, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal))
        {
            var need = pawn.Needs.SingleOrDefault(n => n.Kind == target.Kind);
            findings.UnionWith(target.Unsupported);
            if (need is null) { findings.Add("unknown_need_target:" + target.Id); continue; }
            var (level, fall, travel) = (need.Level, need.FallPerTick, target.TravelTicks);
            if (!double.IsFinite(level) || level < 0 || level > 1 || !double.IsFinite(fall) || fall < 0 || !double.IsFinite(travel) || travel < 0)
                throw new DomainException("invalid needs candidate mechanics");
            // A sleep blocker with no recovery bound plans no sleep.
            if (target.Kind == "rest" && pawn.SleepBlocked) { findings.Add("sleep_blocked:" + pawn.Pawn); continue; }
            var starts = new SortedSet<double> { 0, horizon / 2 };
            var primaryStarts = new HashSet<double> { 0, horizon / 2 };
            if (fall > 0) starts.Add(Math.Max(0, (level - need.Threshold) / fall - travel));
            foreach (var window in windows) { starts.Add(window.Start); primaryStarts.Add(window.Start); }
            if (target.Kind == "rest")
            {
                starts.Add(Math.Max(0, pawn.SleepBlockedUntilTicks - travel));
                if (fall > 0) starts.Add(Math.Max(0, Math.Floor((level - pawn.SleepThreshold) / fall) + 1 - travel));
            }
            var variants = new List<(int Count, double Duration, double Gain)>();
            if (target.Kind == "food" && target.Recovery is NeedRecovery.Ingest { ServiceTicks: var service and > 0, GainPerUnit: var unit and > 0 })
            {
                // Whole quantities are alternatives, not a preselected nutrition target. Bound excessive stacks explicitly.
                var limit = Math.Min(target.AvailableCount, Math.Min(16, (int)Math.Ceiling(1 / unit)));
                if (target.AvailableCount > limit) findings.Add("bounded_food_quantities:" + target.Id);
                for (var count = 1; count <= limit; count++) variants.Add((count, service, unit * count));
            }
            else if (target.Recovery is NeedRecovery.Rate { GainPerTick: var rate and > 0 })
            {
                // Partial and full recovery both survive generation; service duration never comes from a trigger.
                var full = (1 - level) / rate;
                foreach (var duration in new[] { full / 2, full, horizon / 4 }.Distinct().Order())
                    if (duration > 0) variants.Add((0, duration, rate * duration));
            }
            else findings.Add("unknown_need_recovery:" + target.Id);
            if (target.Kind == "food" && fall > 0)
            {
                var foodThreshold = need.Threshold;
                // Repeated observed meals are separate timed uses of the same finite lot, not another food owner.
                // Seed whole-quantity refill trajectories; the shared selector still chooses all actual uses.
                foreach (var variant in variants)
                {
                    var eventStart = Math.Max(0, (level - foodThreshold) / fall);
                    var before = level; double previousEnd = 0;
                    var occurrence = 0;
                    for (; occurrence < 16 && eventStart + variant.Duration <= horizon; occurrence++)
                    {
                        var depart = Math.Max(0, eventStart - travel);
                        starts.Add(depart); primaryStarts.Add(depart);
                        var end = depart + travel + variant.Duration;
                        var after = Math.Min(1, Math.Max(0, before - fall * (end - previousEnd)) + variant.Gain);
                        previousEnd = end; before = after;
                        eventStart = end + Math.Max(0, (after - foodThreshold) / fall);
                    }
                    if (occurrence == 16 && eventStart + variant.Duration <= horizon)
                    { truncated = true; findings.Add("bounded_repeated_food_uses:" + target.Id); }
                }
            }
            IEnumerable<(int Count, double Duration, double Gain)> RestVariants(double start)
            {
                if (target.Recovery is not NeedRecovery.Rate { GainPerTick: var restRate and > 0 }) yield break;
                var full = Math.Max(0, pawn.WakeThreshold - Math.Max(0, level - fall * start)) / restRate;
                foreach (var duration in new[] { full / 2, full, Math.Min(full, horizon / 4) }.Distinct().Order())
                    if (duration > 0) yield return (0, duration, restRate * duration);
            }
            void EndsAt(double end)
            {
                if (target.Kind != "rest" || target.Recovery is not NeedRecovery.Rate { GainPerTick: var restRate and > 0 }) return;
                foreach (var portion in new[] { .5, 1d })
                {
                    // Solve start + portion * (wake - (initial - fall*start))/gain = end.
                    var start = (restRate * end - portion * (pawn.WakeThreshold - level)) / (restRate + portion * fall);
                    if (start >= 0 && level - fall * start >= 0) starts.Add(Math.Max(0, start - travel));
                    var depletedStart = end - portion * pawn.WakeThreshold / restRate;
                    if (depletedStart >= 0 && level - fall * depletedStart <= 0) starts.Add(Math.Max(0, depletedStart - travel));
                }
            }
            foreach (var transition in ForecastMood.ConditionalThoughtTransitions(pawn, horizon))
            {
                starts.Add(Math.Max(0, transition - travel));
                primaryStarts.Add(Math.Max(0, transition - travel));
                EndsAt(transition);
                foreach (var variant in variants) starts.Add(Math.Max(0, transition - travel - variant.Duration));
            }
            foreach (var window in windows)
            {
                EndsAt(window.End);
                foreach (var variant in variants) starts.Add(Math.Max(window.Start, window.End - travel - variant.Duration));
            }
            // Seed observed event boundaries across the horizon before secondary finish-at-event refinements.
            // One past the budget, so a target with more pairs than the whole budget still reads as cut.
            plans.Add((target, level, fall, travel, starts.OrderBy(t => primaryStarts.Contains(t) ? 0 : 1).ThenBy(t => t)
                .SelectMany(depart => (target.Kind == "rest" ? RestVariants(depart + travel) : variants).Select(variant => (depart, variant)))
                .Take(budget + 1).ToList()));
        }
        // Each (start, variant) pair costs one budget unit before any filter. Units go round-robin across need kinds, and
        // within a kind across its targets, so many food targets and counts cannot starve rest of its first starts (#3167).
        var allowance = new int[plans.Count];
        var kinds = Enumerable.Range(0, plans.Count).GroupBy(i => plans[i].Target.Kind, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.ToArray()).ToArray();
        var turn = new int[kinds.Length];
        for (var left = budget; left > 0;)
        {
            var granted = false;
            for (var k = 0; k < kinds.Length && left > 0; k++)
                for (var tried = 0; tried < kinds[k].Length; tried++)
                {
                    var i = kinds[k][turn[k]];
                    turn[k] = (turn[k] + 1) % kinds[k].Length;
                    if (allowance[i] >= plans[i].Pairs.Count) continue;
                    allowance[i]++; left--; granted = true;
                    break;
                }
            if (!granted) break;
        }
        for (var p = 0; p < plans.Count; p++)
        {
            var (target, level, fall, travel, pairs) = plans[p];
            if (allowance[p] < pairs.Count) { truncated = true; findings.Add("needs_generation_truncated:" + target.Kind); }
            foreach (var (depart, variant) in pairs.Take(allowance[p]))
            {
                var start = depart + travel;
                var end = start + variant.Duration;
                if (target.Kind == "rest" && (start < pawn.SleepBlockedUntilTicks
                    || Math.Max(0, level - fall * start) >= pawn.SleepThreshold)) continue;
                if (target.Kind == "food" && target.Recovery is NeedRecovery.Ingest { GainPerUnit: var nutrition }
                    && variant.Count > Math.Ceiling((1 - Math.Max(0, level - fall * end)) / nutrition)) continue;
                if (end > horizon || !windows.Any(w => w.Start <= depart && w.End >= end)) continue;
                // Equivalent algebraic event constructions can differ by floating-point roundoff below a microtick.
                if (!choiceKeys.Add((target.Id, target.Kind, variant.Count, Math.Round(depart, 6), Math.Round(start, 6), Math.Round(end, 6)))) continue;
                choices.Add(new("need/" + pawn.Pawn + "/" + target.Id + "/" + choices.Count,
                    pawn.Pawn, target.Kind, target.Id, target.Resource, variant.Count, depart, start, end,
                    variant.Gain, target.MealThoughts));
            }
        }
        foreach (var need in pawn.Needs)
            if (!choices.Any(c => c.Kind == need.Kind)) findings.Add("no_feasible_need_choice:" + need.Kind);
        if (truncated) findings.Add("needs_generation_truncated");
        return new(EquatableArray.From(choices), EquatableArray.From(findings), truncated);
    }

    /// <summary>Event/interval projection. Productive work and needs share one occupancy union. Food is credited
    /// only at completed eating; interrupted rest/joy credit elapsed service. Break risk is the mood timeline's; no mood currency.
    /// Without mood the result carries no mood timeline (Mood null), for callers that read only needs and capacity (#3467).</summary>
    public static ForecastNeedsResult ForecastNeedsTimeline(NeedsForecastPawn pawn, ForecastCapacityInput input,
        IReadOnlyList<ForecastNeedChoice> selected, IReadOnlyDictionary<string, double>? interruptedAt = null,
        IReadOnlyList<(NeedMemory Thought, double Start)>? granted = null,
        IReadOnlyList<(string Thought, double OffsetDelta, double Start)>? situational = null,
        IReadOnlyList<(string Thought, IReadOnlyList<(double Start, double End, double OffsetDelta)> Spans)>? banded = null,
        IReadOnlyList<ForecastMemoryRenewal>? renewals = null, bool mood = true, bool moodTrajectory = true)
    {
        var core = NeedsCoreOf(pawn, input.HorizonTicks, selected, interruptedAt, granted, renewals);
        var timeline = mood ? ForecastMood.ForecastMoodTimeline(pawn, input.HorizonTicks, selected, interruptedAt, granted, situational,
            [.. banded ?? [], .. ForecastMood.NeedThoughts(core.Needs)], renewals, moodTrajectory) : null;
        return NeedsWithCapacity(pawn, core, input, timeline);
    }

    /// <summary>What a needs timeline settles before its occupancy (#3468): the need metrics, the time the needs themselves
    /// occupy, the memory delta and target, the useful food gains, the findings so far and the service intervals. A search
    /// evaluates a pawn's choices under many occupancies, so this is computed once per distinct choices, grants and renewals.</summary>
    internal sealed record NeedsCore(EquatableArray<ForecastNeedMetric> Needs, ForecastOccupancy[] Occupied, ForecastTerm ThoughtOffsetAtHorizon,
        ForecastTerm MemoryAdjustedMoodTarget, Dictionary<string, double> UsefulFoodGain, string[] Findings, (ForecastNeedChoice Choice, double End)[] Intervals);

    internal static NeedsCore NeedsCoreOf(NeedsForecastPawn pawn, double horizonTicks, IReadOnlyList<ForecastNeedChoice> selected,
        IReadOnlyDictionary<string, double>? interruptedAt, IReadOnlyList<(NeedMemory Thought, double Start)>? granted, IReadOnlyList<ForecastMemoryRenewal>? renewals)
    {
        NeedHorizon(horizonTicks);
        if (selected.Any(c => c.Pawn != pawn.Pawn) || selected.Select(c => c.Id).Distinct().Count() != selected.Count)
            throw new DomainException("needs timeline identity mismatch");
        var findings = new SortedSet<string>(pawn.Unsupported, StringComparer.Ordinal);
        var intervals = selected.Select(c => (Choice: c, End: interruptedAt is not null && interruptedAt.TryGetValue(c.Id, out var at)
            ? Math.Clamp(at, c.Depart, c.End) : c.End)).OrderBy(c => c.Choice.Depart).ToArray();
        if (intervals.Any(c => c.Choice.Depart < 0 || c.Choice.Start < c.Choice.Depart || c.Choice.End <= c.Choice.Start
            || c.Choice.End > horizonTicks || !double.IsFinite(c.Choice.Gain) || c.Choice.Gain < 0)
            || intervals.Zip(intervals.Skip(1)).Any(p => p.First.End > p.Second.Choice.Depart))
            throw new DomainException("overlapping or invalid needs activities");
        var occupied = new List<ForecastOccupancy>();
        if (!Voluntary(pawn.State)) occupied.Add(new("needs_state", pawn.Pawn, 0.0, horizonTicks, ForecastOccupancyState.Active));
        occupied.AddRange(intervals.Select(c => c.Choice.Occupancy with { End = c.End }));
        var sleepEnds = SleepServiceEnds(pawn, selected, interruptedAt, findings);
        var metrics = new List<ForecastNeedMetric>();
        var usefulFood = new Dictionary<string, double>(StringComparer.Ordinal);

        Span<double> split = stackalloc double[5];
        List<ForecastNeedSpan>? zeroSpans = null;
        foreach (var need in pawn.Needs)
        {
            findings.UnionWith(need.Unsupported);
            if (need.Kind == "rest" && sleepEnds is null)
            {
                var unprojected = new ForecastTerm.Unassessed("sleep_unprojected");
                metrics.Add(new(need.Kind, unprojected, unprojected, unprojected, unprojected) { ZeroTicks = unprojected, ThresholdDepthTicks = unprojected });
                occupied.Add(new("unknown_sleep", pawn.Pawn, 0d, 0d, ForecastOccupancyState.Untimed));
                continue;
            }
            var (level, fall, threshold) = (need.Level, need.FallPerTick, need.Threshold);
            if (!double.IsFinite(level) || level < 0 || level > 1 || !double.IsFinite(fall) || fall < 0
                || !double.IsFinite(threshold) || threshold < 0 || threshold > 1) throw new DomainException("invalid need mechanics");
            double deficit = 0, exposure = 0, waste = 0, zero = 0, depth = 0;
            zeroSpans = null;
            List<ForecastNeedStageSpan>? stageSpans = null;
            var events = new SortedSet<double> { 0, horizonTicks };
            // Unrelated service boundaries cannot change this need trajectory. Splitting there
            // introduces rounding differences that can incorrectly dominate later consequences.
            foreach (var row in intervals.Where(r => r.Choice.Kind == need.Kind))
            { events.Add(row.Choice.Start); events.Add(row.End); }
            if (need.Kind == "rest" && sleepEnds is not null)
                foreach (var end in sleepEnds.Values) events.Add(end);
            var points = events.Where(t => t <= horizonTicks).ToArray();
            for (var i = 0; i < points.Length - 1; i++)
            {
                var from = points[i]; var to = points[i + 1];
                ForecastNeedChoice? active = null;
                foreach (var row in intervals)
                    if (row.Choice.Kind == need.Kind && row.Choice.Start <= from && row.End > from
                        && (need.Kind != "rest" || sleepEnds![row.Choice.Id] > from))
                    { active = row.Choice; break; }
                var gain = active is not null && need.Kind != "food"
                    ? active.Gain / (active.End - active.Start) : -fall;
                var raw = level + gain * (to - from);
                var next = Math.Clamp(raw, 0, 1);
                // Integrate clipped piecewise linear deficit exactly at saturation and threshold crossings.
                split[0] = 0; split[1] = to - from;
                var cutCount = 2;
                if (gain != 0)
                    for (var boundary = 0; boundary < 3; boundary++)
                    {
                        var bound = boundary == 0 ? 0.0 : boundary == 1 ? threshold : 1.0;
                        var t = (bound - level) / gain;
                        if (t > 0 && t < to - from && !split[..cutCount].Contains(t)) split[cutCount++] = t;
                    }
                split[..cutCount].Sort();
                for (var k = 0; k < cutCount - 1; k++)
                {
                    var a = split[k]; var b = split[k + 1];
                    var lo = Math.Clamp(level + gain * a, 0, 1); var hi = Math.Clamp(level + gain * b, 0, 1);
                    deficit += (1 - (lo + hi) / 2) * (b - a);
                    if ((lo + hi) / 2 < threshold) exposure += b - a;
                    depth += Math.Max(0, threshold - (lo + hi) / 2) * (b - a);
                    if (lo == 0 && hi == 0)
                    {
                        zero += b - a;
                        zeroSpans ??= [];
                        // Contiguous zero pieces (split at an event or a cut) are one span.
                        if (zeroSpans.Count > 0 && zeroSpans[^1].End == from + a) zeroSpans[^1] = zeroSpans[^1] with { End = from + b };
                        else zeroSpans.Add(new(from + a, from + b));
                    }
                    if (need.Kind is "food" or "rest") AddStageSpans(stageSpans ??= [], need.Kind, threshold, from + a, from + b, lo, hi);
                    // Zero food/rest means physiological capacity is unassessed, never free productive labor.
                    if (need.Kind is "food" or "rest" && lo == 0 && hi == 0)
                        occupied.Add(new("need_zero/" + need.Kind + "/" + from + "/" + k, pawn.Pawn,
                            from + a, from + b, ForecastOccupancyState.Active));
                }
                waste += Math.Max(0, raw - 1);
                level = next;
                foreach (var eat in intervals)
                {
                    if (eat.Choice.Kind != need.Kind || need.Kind != "food" || eat.End != to || eat.End != eat.Choice.End) continue;
                    usefulFood[eat.Choice.Id] = Math.Min(1 - level, eat.Choice.Gain);
                    waste += Math.Max(0, level + eat.Choice.Gain - 1); level = Math.Min(1, level + eat.Choice.Gain);
                }
            }
            metrics.Add(new(need.Kind, new ForecastTerm.Assessed(level), new ForecastTerm.Assessed(deficit), new ForecastTerm.Assessed(exposure), new ForecastTerm.Assessed(waste))
            {
                ZeroTicks = new ForecastTerm.Assessed(zero), ThresholdDepthTicks = new ForecastTerm.Assessed(depth), ZeroSpans = zeroSpans is null ? default : EquatableArray.From(zeroSpans),
                StageSpans = stageSpans is null ? default : EquatableArray.From(stageSpans),
            });
        }
        ForecastTerm thoughtDelta = new ForecastTerm.Unassessed("thought_projection_unavailable");
        if (pawn.Mood is { } mood)
        {
            var memories = mood.Memories;
            static double Remaining(NeedMemory m, double elapsed) => ForecastMood.MemoryAt(m, elapsed, elapsed);
            var complete = true;
            if (ForecastMood.StackedMemoryFinding(memories) is { } stacked) findings.Add(stacked);
            var added = intervals.Where(r => r.Choice.Kind == "food" && r.End == r.Choice.End)
                .SelectMany(r => r.Choice.Thoughts.Select(t => (Thought: t, Start: r.End))).Concat(granted ?? []).ToArray();
            var schedule = ForecastMood.RenewalSchedule(memories, renewals, added.Select(a => a.Thought.Id));
            if (schedule is null) { findings.Add("unsupported_memory_renewal_or_stacking"); complete = false; }
            // A stackLimit-1 meal renews the memory live before it (#3598); any other shared def is not additive evidence.
            var planned = ForecastMood.PlannedMemoryEnds(memories, added);
            if (planned is null) { findings.Add("unsupported_meal_memory_overlap"); complete = false; }
            // The share of each observed memory left at the horizon: its renewals' expected cut, nothing once a planned memory renewed it.
            double ObservedShare(string id) => planned is { } p && p.ObservedEnds.TryGetValue(id, out var at) && at <= horizonTicks ? 0
                : schedule is { } s ? s.Observed[id].At(horizonTicks) : 1;
            var delta = memories.Sum(m => Remaining(m, horizonTicks) * ObservedShare(m.Id) - m.Offset);
            if (schedule is { } renewedSchedule)
                foreach (var gain in renewedSchedule.Gains.Where(g => g.Start <= horizonTicks))
                    delta += Remaining(gain.Thought, horizonTicks - gain.Start) * gain.Share.At(horizonTicks);
            for (var i = 0; i < added.Length; i++)
            {
                var (thought, start) = added[i];
                if (planned is { } ends && ends.Ends[i] <= horizonTicks) continue;
                delta += Remaining(thought, horizonTicks - start);
            }
            thoughtDelta = complete ? new ForecastTerm.Assessed(delta) : new ForecastTerm.Unassessed("memory_projection_unsupported");
        }
        else findings.Add("unknown_thought_expiry");
        ForecastTerm target = thoughtDelta is ForecastTerm.Assessed(var offset) && pawn.Mood is { } moodNow ? new ForecastTerm.Assessed(Math.Clamp(moodNow.Target + offset / 100, 0, 1)) : new ForecastTerm.Unassessed("mood_target_unprojected");
        findings.Add("need_rates_constant_current_conditions");
        findings.Add("room_mood_changes_unassessed");
        return new(EquatableArray.From(metrics), [.. occupied], thoughtDelta, target, usefulFood, [.. findings], intervals);
    }

    /// <summary>A needs core under the given occupancy: the services must not overlap protected time and must lie in the
    /// pawn's free windows (<paramref name="initialCapacity"/>, the capacity of <paramref name="input"/> without timetable
    /// protection, when the caller holds it); the capacity then protects that time and the needs' own.</summary>
    internal static ForecastNeedsResult NeedsWithCapacity(NeedsForecastPawn pawn, NeedsCore core, ForecastCapacityInput input, ForecastMoodResult? timeline,
        ForecastCapacityResult? initialCapacity = null)
    {
        if (input.Pawn.Id.Value != pawn.Pawn) throw new DomainException("needs timeline identity mismatch");
        var intervals = core.Intervals;
        var unknownProtection = input.Occupancy is not ForecastProtection.Read;
        var protectedTime = input.Occupancy is ForecastProtection.Read read ? read.Intervals : default(EquatableArray<ForecastOccupancy>);
        if (!unknownProtection && intervals.Any(c => protectedTime.Any(o => o.Pawn == pawn.Pawn && o.State == ForecastOccupancyState.Active
            && o.Start < c.End && o.End > c.Choice.Depart)))
            throw new DomainException("needs overlap protected work");
        var unprotected = input.ProtectTimetable ? input with { ProtectTimetable = false } : input;
        initialCapacity ??= ForecastCapacity.ForecastPawnCapacityOf(unprotected);
        if (intervals.Length > 0 && (!Voluntary(pawn.State) || initialCapacity.Capacity.Windows is not ForecastCapacityWindows.Read(var free)
            || intervals.Any(c => !free.Any(w => w.Start <= c.Choice.Depart && w.End >= c.End))))
            throw new DomainException("needs occupy unavailable pawn time");
        ForecastProtection occupancy = new ForecastProtection.Truncated();
        if (!unknownProtection)
        {
            var rows = new ForecastOccupancy[protectedTime.Count + core.Occupied.Length];
            for (var i = 0; i < protectedTime.Count; i++) rows[i] = protectedTime[i];
            core.Occupied.CopyTo(rows, protectedTime.Count);
            occupancy = new ForecastProtection.Read(EquatableArray.Wrap(rows));
        }
        var capacity = ForecastCapacity.ForecastPawnCapacityOf(unprotected with { Occupancy = occupancy });
        // The core's findings are sorted and distinct; the rest are merged in.
        var findings = new List<string>(core.Findings.Length + capacity.Findings.Length + 2);
        findings.AddRange(core.Findings);
        findings.AddRange(capacity.Findings);
        if (unknownProtection) findings.Add("unknown_protected_time");
        if (timeline is ForecastMoodResult.Unprojected or ForecastMoodResult.Projected { Breaks: ForecastMoodBreaks.Unassessed }) findings.Add("mental_break_probability_unassessed");
        findings.Sort(StringComparer.Ordinal);
        var distinct = 0;
        for (var i = 0; i < findings.Count; i++) if (i == 0 || findings[i] != findings[i - 1]) findings[distinct++] = findings[i];
        var merged = new string[distinct];
        findings.CopyTo(0, merged, 0, distinct);
        return new(core.Needs, core.ThoughtOffsetAtHorizon, core.MemoryAdjustedMoodTarget, capacity, EquatableArray.Wrap(merged))
        { Mood = timeline, UsefulFoodGain = core.UsefulFoodGain };
    }

    /// <summary>One physiological sleep-service projection shared by rest recovery and conditional mood.
    /// Remaining assigned bed time after native waking stays occupied, but is not counted as sleep.</summary>
    internal static Dictionary<string, double>? SleepServiceEnds(NeedsForecastPawn pawn,
        IReadOnlyList<ForecastNeedChoice> selected, IReadOnlyDictionary<string, double>? interruptedAt, SortedSet<string> findings)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var sleeps = selected.Where(c => c.Kind == "rest").OrderBy(c => c.Start).ToArray();
        if (sleeps.Length == 0) return result;
        var need = pawn.Needs.SingleOrDefault(n => n.Kind == "rest");
        if (need is null) { findings.Add("unknown_sleep_mechanics"); return null; }
        var (level, fall, wake, threshold) = (need.Level, need.FallPerTick, pawn.WakeThreshold, pawn.SleepThreshold);
        if (!double.IsFinite(wake) || wake <= 0 || wake > 1 || !double.IsFinite(threshold) || threshold <= 0 || threshold >= wake)
            throw new DomainException("invalid sleep thresholds");
        double cursor = 0;
        foreach (var choice in sleeps)
        {
            var end = interruptedAt is not null && interruptedAt.TryGetValue(choice.Id, out var stopped)
                ? Math.Clamp(stopped, choice.Depart, choice.End) : choice.End;
            if (end <= choice.Start) { result.Add(choice.Id, choice.Start); continue; }
            level = Math.Max(0, level - fall * (choice.Start - cursor));
            var continuing = choice.Start == 0 && !pawn.Awake;
            if (!continuing && pawn.Needs.SingleOrDefault(n => n.Kind == "food") is { } food)
            {
                var (nutrition, hunger) = (food.Level, food.FallPerTick);
                double ateAt = 0;
                foreach (var meal in selected.Where(c => c.Kind == "food" && c.End <= choice.Start
                    && (interruptedAt is null || !interruptedAt.TryGetValue(c.Id, out var stoppedAt) || stoppedAt >= c.End)).OrderBy(c => c.End))
                {
                    nutrition = Math.Min(1, Math.Max(0, nutrition - hunger * (meal.End - ateAt)) + meal.Gain);
                    ateAt = meal.End;
                }
                nutrition = Math.Max(0, nutrition - hunger * (choice.Start - ateAt));
                if (nutrition == 0 && !pawn.SleepWhileStarving)
                { findings.Add("sleep_requires_observed_food_recovery:" + choice.Id); return null; }
            }
            if (!continuing && (pawn.SleepBlocked || choice.Start < pawn.SleepBlockedUntilTicks || level >= threshold))
            { findings.Add("sleep_activation_unassessed:" + choice.Id); return null; }
            var rate = choice.Gain / (choice.End - choice.Start);
            if (!double.IsFinite(rate) || rate <= 0 || !double.IsFinite(level) || !double.IsFinite(fall) || fall < 0)
            { findings.Add("unknown_sleep_recovery:" + choice.Id); return null; }
            var serviceEnd = Math.Min(end, choice.Start + Math.Max(0, wake - level) / rate);
            result.Add(choice.Id, serviceEnd);
            level = Math.Min(1, level + rate * (serviceEnd - choice.Start));
            level = Math.Max(0, level - fall * (end - serviceEnd));
            if (serviceEnd < end && level < threshold)
            { findings.Add("sleep_reactivation_unassessed:" + choice.Id); return null; }
            if (serviceEnd < end) findings.Add("sleep_service_capped_at_native_wake:" + choice.Id);
            cursor = end;
        }
        return result;
    }
    // Need_Food.PercentageThreshUrgentlyHungry is 0.4 of FoodLevelPercentageWantEat and PercentageThreshHungry (the food
    // row's observed Threshold) 0.8 of it, so urgent hunger starts at half the hungry threshold.
    private const double UrgentlyHungryOfHungry = 0.5;

    // Need_Rest.CurCategory: very tired below ThreshVeryTired, exhausted below 0.01 (tired below ThreshTired, the rest row's observed Threshold).
    private const double RestVeryTired = 0.14;
    private const double RestExhausted = 0.01;

    /// <summary>
    /// The need thought's stage at a level (#3598), -1 for none: ThoughtWorker_NeedFood gives stage 0 when hungry, 1 when
    /// urgently hungry and 2 plus the malnutrition stage when starving (level 0); ThoughtWorker_NeedRest stage 0 when tired,
    /// 1 when very tired and 2 when exhausted. <paramref name="threshold"/> is the row's observed hungry or tired threshold.
    /// A starving pawn is held at stage 2, the malnutrition stages past the first left unpriced.
    /// </summary>
    internal static int NeedThoughtStage(string kind, double level, double threshold) => kind switch
    {
        "food" => level <= 0 ? 2 : level < threshold * UrgentlyHungryOfHungry ? 1 : level < threshold ? 0 : -1,
        "rest" => level < RestExhausted ? 2 : level < RestVeryTired ? 1 : level < threshold ? 0 : -1,
        _ => -1,
    };

    /// <summary>Appends the need thought's stages over one linear piece of the level (from <paramref name="from"/> to
    /// <paramref name="to"/>), cut where it crosses a stage boundary; the walk already cuts at 0 and the threshold.</summary>
    private static void AddStageSpans(List<ForecastNeedStageSpan> spans, string kind, double threshold, double start, double end, double from, double to)
    {
        if (end <= start) return;
        Span<double> cuts = stackalloc double[4];
        var count = 0;
        cuts[count++] = 0;
        if (from != to)
            foreach (var bound in kind == "food" ? (ReadOnlySpan<double>)[threshold * UrgentlyHungryOfHungry] : [RestVeryTired, RestExhausted])
                if ((bound - from) / (to - from) is var f and > 0 and < 1) cuts[count++] = f;
        cuts[count++] = 1;
        cuts[..count].Sort();
        for (var i = 0; i < count - 1; i++)
        {
            var s = i == 0 ? start : start + (end - start) * cuts[i];
            var e = i == count - 2 ? end : start + (end - start) * cuts[i + 1];
            if (e <= s) continue;
            var stage = NeedThoughtStage(kind, from + (to - from) * (cuts[i] + cuts[i + 1]) / 2, threshold);
            if (spans.Count > 0 && spans[^1].Stage == stage && spans[^1].End == s) spans[^1] = spans[^1] with { End = e };
            else spans.Add(new(s, e, stage));
        }
    }

    internal static void NeedHorizon(double horizon)
    {
        if (!double.IsFinite(horizon) || horizon <= 0 || horizon > GameTime.TicksPerDay)
            throw new DomainException("needs horizon must be positive and at most one day");
    }
}
