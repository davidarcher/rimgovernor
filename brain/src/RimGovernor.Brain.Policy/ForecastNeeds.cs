using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record ForecastNeedChoice(string Id, string Pawn, string Kind, string Target, string Resource,
    int Count, double Depart, double Start, double End, double Gain, EquatableArray<NeedMemory> Thoughts)
{
    public ForecastOccupancy Occupancy => new(Id, Pawn, Known(Depart), Known(End), ForecastOccupancyState.Active);
    public ForecastActivity Activity => new(Id, Target, Target, [])
    { Inputs = Kind == "food" ? [new(new(Resource), Known((double)Count))] : [] };
}
public sealed record ForecastNeedMetric(string Kind, Fact<double> FinalLevel, Fact<double> DeficitTicks,
    Fact<double> ThresholdExposureTicks, Fact<double> WastedGain)
{ public Fact<double> ZeroTicks { get; init; } public Fact<double> ThresholdDepthTicks { get; init; } }
public sealed record ForecastNeedsResult(EquatableArray<ForecastNeedMetric> Needs,
    Fact<double> ThoughtOffsetAtHorizon, Fact<double> MemoryAdjustedMoodTarget,
    ForecastCapacityResult Capacity, EquatableArray<string> Findings)
{
    public ForecastMoodResult? Mood { get; init; }
    public IReadOnlyDictionary<string, double> UsefulFoodGain { get; init; } = new Dictionary<string, double>();
}
public sealed record ForecastNeedsCandidates(EquatableArray<ForecastNeedChoice> Choices,
    EquatableArray<string> Findings, bool Truncated);
public sealed record ForecastNeedsReview(NeedsForecastRead Observation,
    IReadOnlyDictionary<string, ForecastNeedsCandidates> Alternatives);

public static partial class PolicyFunctions
{
    /// <summary>Ordinary review exposes all generated choices for coupled comparison, not a shadow winner.</summary>
    public static ForecastNeedsReview? ReviewNeedsAlternatives(NeedsForecastRead? observed, WorkResourceView? resources,
        double horizon = GameTime.TicksPerDay)
    {
        if (observed is null || resources is null) return null;
        return new(observed, observed.Pawns.ToDictionary(p => p.Pawn, p => GenerateNeedsAlternatives(p,
            NeedsWorkCapacity(resources, p.Pawn, horizon), horizon, observed.Truncated), StringComparer.Ordinal));
    }

    /// <summary>Native timetable is descriptive here. Only actual current work and journal ownership protect time.
    /// Explicit player obligations can be supplied to ForecastNeedsTimeline; legacy timetable writers are retired by #3135.</summary>
    public static ForecastCapacityResult NeedsWorkCapacity(WorkResourceView view, string pawn, double horizon)
        => WorkCapacityOf(view, pawn, horizon, protectTimetable: false);

    public static ForecastNeedsCandidates GenerateNeedsAlternatives(NeedsForecastPawn pawn, ForecastCapacityResult capacity,
        double horizon, bool observedTruncated = false, int budget = 256)
    {
        NeedHorizon(horizon);
        if (budget < 1 || budget > 4096) throw new DomainException("invalid needs generation budget");
        var choices = new List<ForecastNeedChoice>();
        var choiceKeys = new HashSet<(string Target, string Kind, int Count, double Depart, double Start, double End)>();
        var findings = new SortedSet<string>(pawn.Unsupported, StringComparer.Ordinal);
        var truncated = observedTruncated || pawn.Targets.Count > 256;
        var expansions = 0;
        if (pawn.State != "eligible") return new([], ["needs_state:" + pawn.State], truncated);
        if (!capacity.Capacity.Windows.TryGetValue(out var windows)) return new([], ["unknown_needs_capacity"], truncated);
        foreach (var target in pawn.Targets.Take(256).OrderBy(t => t.Kind, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal))
        {
            if (expansions >= budget) { truncated = true; break; }
            var need = pawn.Needs.SingleOrDefault(n => n.Kind == target.Kind);
            findings.UnionWith(target.Unsupported);
            if (need is null || !need.Level.TryGetValue(out var level) || !need.FallPerTick.TryGetValue(out var fall)
                || !target.TravelTicks.TryGetValue(out var travel) || travel < 0)
            { findings.Add("unknown_need_target:" + target.Id); continue; }
            if (!double.IsFinite(level) || level < 0 || level > 1 || !double.IsFinite(fall) || fall < 0 || !double.IsFinite(travel))
                throw new DomainException("invalid needs candidate mechanics");
            if (target.Kind == "rest" && (!pawn.SleepThreshold.IsKnown || !pawn.WakeThreshold.IsKnown || !pawn.SleepBlockedUntilTicks.IsKnown))
            { findings.Add("unknown_sleep_activation:" + pawn.Pawn); continue; }
            var starts = new SortedSet<double> { 0, horizon / 2 };
            var primaryStarts = new HashSet<double> { 0, horizon / 2 };
            if (need.Threshold.TryGetValue(out var threshold) && fall > 0)
                starts.Add(Math.Max(0, (level - threshold) / fall - travel));
            foreach (var window in windows) { starts.Add(window.Start); primaryStarts.Add(window.Start); }
            if (target.Kind == "rest")
            {
                starts.Add(Math.Max(0, pawn.SleepBlockedUntilTicks.Value - travel));
                if (fall > 0) starts.Add(Math.Max(0, Math.Floor((level - pawn.SleepThreshold.Value) / fall) + 1 - travel));
            }
            var variants = new List<(int Count, double Duration, double Gain)>();
            if (target.Kind == "food" && target.ServiceTicks.TryGetValue(out var service)
                && target.GainPerUnit.TryGetValue(out var unit) && unit > 0 && service > 0)
            {
                // Whole quantities are alternatives, not a preselected nutrition target. Bound excessive stacks explicitly.
                var limit = Math.Min(target.AvailableCount, Math.Min(16, (int)Math.Ceiling(1 / unit)));
                if (target.AvailableCount > limit) findings.Add("bounded_food_quantities:" + target.Id);
                for (var count = 1; count <= limit; count++) variants.Add((count, service, unit * count));
            }
            else if (target.GainPerTick.TryGetValue(out var rate) && rate > 0)
            {
                // Partial and full recovery both survive generation; service duration never comes from a trigger.
                var full = (1 - level) / rate;
                foreach (var duration in new[] { full / 2, full, horizon / 4 }.Distinct().Order())
                    if (duration > 0) variants.Add((0, duration, rate * duration));
            }
            else findings.Add("unknown_need_recovery:" + target.Id);
            if (target.Kind == "food" && fall > 0 && need.Threshold.TryGetValue(out var foodThreshold))
            {
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
                if (!target.GainPerTick.TryGetValue(out var restRate) || restRate <= 0) yield break;
                var full = Math.Max(0, pawn.WakeThreshold.Value - Math.Max(0, level - fall * start)) / restRate;
                foreach (var duration in new[] { full / 2, full, Math.Min(full, horizon / 4) }.Distinct().Order())
                    if (duration > 0) yield return (0, duration, restRate * duration);
            }
            void EndsAt(double end)
            {
                if (target.Kind != "rest" || !target.GainPerTick.TryGetValue(out var restRate) || restRate <= 0) return;
                foreach (var portion in new[] { .5, 1d })
                {
                    // Solve start + portion * (wake - (initial - fall*start))/gain = end.
                    var start = (restRate * end - portion * (pawn.WakeThreshold.Value - level)) / (restRate + portion * fall);
                    if (start >= 0 && level - fall * start >= 0) starts.Add(Math.Max(0, start - travel));
                    var depletedStart = end - portion * pawn.WakeThreshold.Value / restRate;
                    if (depletedStart >= 0 && level - fall * depletedStart <= 0) starts.Add(Math.Max(0, depletedStart - travel));
                }
            }
            foreach (var transition in ConditionalThoughtTransitions(pawn, horizon))
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
            foreach (var depart in starts.OrderBy(t => primaryStarts.Contains(t) ? 0 : 1).ThenBy(t => t).Take(budget))
            foreach (var variant in (target.Kind == "rest" ? RestVariants(depart + travel) : variants).Take(budget))
            {
                if (expansions++ >= budget) { truncated = true; break; }
                var start = depart + travel;
                var end = start + variant.Duration;
                if (target.Kind == "rest" && (start < pawn.SleepBlockedUntilTicks.Value
                    || Math.Max(0, level - fall * start) >= pawn.SleepThreshold.Value)) continue;
                if (target.Kind == "food" && target.GainPerUnit.TryGetValue(out var nutrition)
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
    /// only at completed eating; interrupted rest/joy credit elapsed service. No break probability or mood currency.</summary>
    public static ForecastNeedsResult ForecastNeedsTimeline(NeedsForecastPawn pawn, ForecastCapacityInput input,
        IReadOnlyList<ForecastNeedChoice> selected, IReadOnlyDictionary<string, double>? interruptedAt = null)
    {
        NeedHorizon(input.HorizonTicks);
        if (input.Pawn.Id.Value != pawn.Pawn || selected.Any(c => c.Pawn != pawn.Pawn)
            || selected.Select(c => c.Id).Distinct().Count() != selected.Count)
            throw new DomainException("needs timeline identity mismatch");
        var findings = new SortedSet<string>(pawn.Unsupported, StringComparer.Ordinal);
        var intervals = selected.Select(c => (Choice: c, End: interruptedAt is not null && interruptedAt.TryGetValue(c.Id, out var at)
            ? Math.Clamp(at, c.Depart, c.End) : c.End)).OrderBy(c => c.Choice.Depart).ToArray();
        if (intervals.Any(c => c.Choice.Depart < 0 || c.Choice.Start < c.Choice.Depart || c.Choice.End <= c.Choice.Start
            || c.Choice.End > input.HorizonTicks || !double.IsFinite(c.Choice.Gain) || c.Choice.Gain < 0)
            || intervals.Zip(intervals.Skip(1)).Any(p => p.First.End > p.Second.Choice.Depart))
            throw new DomainException("overlapping or invalid needs activities");
        if (!input.Occupancy.TryGetValue(out var protectedTime)) findings.Add("unknown_protected_time");
        else if (intervals.Any(c => protectedTime.Any(o => o.Pawn == pawn.Pawn && o.State == ForecastOccupancyState.Active
            && o.Start.IsKnown && o.End.IsKnown && o.Start.Value < c.End && o.End.Value > c.Choice.Depart)))
            throw new DomainException("needs overlap protected work");
        var initialCapacity = ForecastPawnCapacityOf(input with { ProtectTimetable = false });
        if (intervals.Length > 0 && (pawn.State != "eligible" || !initialCapacity.Capacity.Windows.TryGetValue(out var free)
            || intervals.Any(c => !free.Any(w => w.Start <= c.Choice.Depart && w.End >= c.End))))
            throw new DomainException("needs occupy unavailable pawn time");
        var occupied = protectedTime.ToList();
        if (pawn.State != "eligible") occupied.Add(new("needs_state", pawn.Pawn, Known(0.0), Known(input.HorizonTicks), ForecastOccupancyState.Active));
        occupied.AddRange(intervals.Select(c => c.Choice.Occupancy with { End = Known(c.End) }));
        var sleepEnds = SleepServiceEnds(pawn, selected, interruptedAt, findings);
        var metrics = new List<ForecastNeedMetric>();
        var usefulFood = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var need in pawn.Needs)
        {
            findings.UnionWith(need.Unsupported);
            if (need.Kind == "rest" && sleepEnds is null)
            {
                metrics.Add(new(need.Kind, default, default, default, default));
                occupied.Add(new("unknown_sleep", pawn.Pawn, Known(0d), default, ForecastOccupancyState.Active));
                continue;
            }
            if (!need.Level.TryGetValue(out var level) || !need.FallPerTick.TryGetValue(out var fall)
                || !need.Threshold.TryGetValue(out var threshold))
            {
                metrics.Add(new(need.Kind, default, default, default, default));
                occupied.Add(new("unknown_need/" + need.Kind, pawn.Pawn, Known(0.0), default, ForecastOccupancyState.Active));
                findings.Add("unknown_need_mechanics:" + need.Kind); continue;
            }
            if (!double.IsFinite(level) || level < 0 || level > 1 || !double.IsFinite(fall) || fall < 0
                || !double.IsFinite(threshold) || threshold < 0 || threshold > 1) throw new DomainException("invalid need mechanics");
            double deficit = 0, exposure = 0, waste = 0, zero = 0, depth = 0;
            var events = new SortedSet<double> { 0, input.HorizonTicks };
            foreach (var row in intervals) { events.Add(row.Choice.Start); events.Add(row.End); }
            if (sleepEnds is not null) foreach (var end in sleepEnds.Values) events.Add(end);
            var points = events.Where(t => t <= input.HorizonTicks).ToArray();
            for (var i = 0; i < points.Length - 1; i++)
            {
                var from = points[i]; var to = points[i + 1];
                var active = intervals.FirstOrDefault(r => r.Choice.Kind == need.Kind && r.Choice.Start <= from && r.End > from
                    && (need.Kind != "rest" || sleepEnds![r.Choice.Id] > from));
                var gain = active.Choice is not null && need.Kind != "food"
                    ? active.Choice.Gain / (active.Choice.End - active.Choice.Start) : -fall;
                var raw = level + gain * (to - from);
                var next = Math.Clamp(raw, 0, 1);
                // Integrate clipped piecewise linear deficit exactly at saturation and threshold crossings.
                var cuts = new SortedSet<double> { 0, to - from };
                if (gain != 0) foreach (var bound in new[] { 0.0, threshold, 1.0 })
                { var t = (bound - level) / gain; if (t > 0 && t < to - from) cuts.Add(t); }
                var split = cuts.ToArray();
                for (var k = 0; k < split.Length - 1; k++)
                {
                    var a = split[k]; var b = split[k + 1];
                    var lo = Math.Clamp(level + gain * a, 0, 1); var hi = Math.Clamp(level + gain * b, 0, 1);
                    deficit += (1 - (lo + hi) / 2) * (b - a);
                    if ((lo + hi) / 2 < threshold) exposure += b - a;
                    depth += Math.Max(0, threshold - (lo + hi) / 2) * (b - a);
                    if (lo == 0 && hi == 0) zero += b - a;
                    // Zero food/rest means physiological capacity is unassessed, never free productive labor.
                    if (need.Kind is "food" or "rest" && lo == 0 && hi == 0)
                        occupied.Add(new("need_zero/" + need.Kind + "/" + from + "/" + k, pawn.Pawn,
                            Known(from + a), Known(from + b), ForecastOccupancyState.Active));
                }
                waste += Math.Max(0, raw - 1);
                level = next;
                foreach (var eat in intervals.Where(r => r.Choice.Kind == need.Kind && need.Kind == "food"
                    && r.End == to && r.End == r.Choice.End))
                {
                    usefulFood[eat.Choice.Id] = Math.Min(1 - level, eat.Choice.Gain);
                    waste += Math.Max(0, level + eat.Choice.Gain - 1); level = Math.Min(1, level + eat.Choice.Gain);
                }
            }
            metrics.Add(new(need.Kind, Known(level), Known(deficit), Known(exposure), Known(waste))
            { ZeroTicks = Known(zero), ThresholdDepthTicks = Known(depth) });
        }
        Fact<double> thoughtDelta = default;
        if (pawn.Memories.TryGetValue(out var memories) && memories.All(m => m.Permanent || m.RemainingTicks.IsKnown) && memories.Select(m => m.Id).Distinct().Count() == memories.Count)
        {
            double Remaining(NeedMemory m, double elapsed) => m.Permanent ? m.Offset : m.RemainingTicks.Value <= elapsed ? 0
                : m.Fades ? m.Offset * (1 - elapsed / m.RemainingTicks.Value) : m.Offset;
            var complete = true;
            var mealIds = new HashSet<string>(StringComparer.Ordinal);
            var delta = memories.Sum(m => Remaining(m, input.HorizonTicks) - m.Offset);
            foreach (var meal in intervals.Where(r => r.Choice.Kind == "food" && r.End == r.Choice.End))
                foreach (var thought in meal.Choice.Thoughts)
                {
                    if (!thought.Permanent && !thought.RemainingTicks.IsKnown) { findings.Add("unknown_meal_thought_duration"); complete = false; continue; }
                    // Same-memory renewal/stacking is not additive evidence.
                    if (memories.Any(m => m.Id == thought.Id) || !mealIds.Add(thought.Id)) { findings.Add("unsupported_meal_memory_overlap:" + thought.Id); complete = false; continue; }
                    delta += Remaining(thought, input.HorizonTicks - meal.End);
                }
            thoughtDelta = complete ? Known(delta) : default;
        }
        else findings.Add("unknown_thought_expiry");
        var target = thoughtDelta.IsKnown && pawn.MoodTarget.IsKnown ? Known(Math.Clamp(pawn.MoodTarget.Value + thoughtDelta.Value / 100, 0, 1)) : default;
        var capacity = ForecastPawnCapacityOf(input with { ProtectTimetable = false,
            Occupancy = input.Occupancy.IsKnown ? Known(EquatableArray.From(occupied)) : default });
        findings.UnionWith(capacity.Findings);
        findings.Add("need_rates_constant_current_conditions");
        findings.Add("mental_break_probability_unassessed");
        findings.Add("need_category_and_room_mood_changes_unassessed");
        return new(EquatableArray.From(metrics), thoughtDelta, target, capacity, EquatableArray.From(findings))
        { Mood = ForecastMoodTimeline(pawn, input.HorizonTicks, selected, interruptedAt), UsefulFoodGain = usefulFood };
    }
    /// <summary>One physiological sleep-service projection shared by rest recovery and conditional mood.
    /// Remaining assigned bed time after native waking stays occupied, but is not counted as sleep.</summary>
    private static Dictionary<string, double>? SleepServiceEnds(NeedsForecastPawn pawn,
        IReadOnlyList<ForecastNeedChoice> selected, IReadOnlyDictionary<string, double>? interruptedAt, SortedSet<string> findings)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var sleeps = selected.Where(c => c.Kind == "rest").OrderBy(c => c.Start).ToArray();
        if (sleeps.Length == 0) return result;
        var need = pawn.Needs.SingleOrDefault(n => n.Kind == "rest");
        if (need is null || !need.Level.TryGetValue(out var level) || !need.FallPerTick.TryGetValue(out var fall)
            || !pawn.WakeThreshold.TryGetValue(out var wake) || !pawn.SleepThreshold.TryGetValue(out var threshold)
            || !pawn.Awake.IsKnown || !double.IsFinite(wake) || wake <= 0 || wake > 1
            || !double.IsFinite(threshold) || threshold <= 0 || threshold >= wake)
        { findings.Add("unknown_sleep_mechanics"); return null; }
        double cursor = 0;
        foreach (var choice in sleeps)
        {
            var end = interruptedAt is not null && interruptedAt.TryGetValue(choice.Id, out var stopped)
                ? Math.Clamp(stopped, choice.Depart, choice.End) : choice.End;
            if (end <= choice.Start) { result.Add(choice.Id, choice.Start); continue; }
            level = Math.Max(0, level - fall * (choice.Start - cursor));
            var continuing = choice.Start == 0 && pawn.Awake == Known(false);
            if (!continuing && pawn.Needs.SingleOrDefault(n => n.Kind == "food") is { } food)
            {
                if (!food.Level.TryGetValue(out var nutrition) || !food.FallPerTick.TryGetValue(out var hunger))
                { findings.Add("sleep_food_availability_unassessed:" + choice.Id); return null; }
                double ateAt = 0;
                foreach (var meal in selected.Where(c => c.Kind == "food" && c.End <= choice.Start
                    && (interruptedAt is null || !interruptedAt.TryGetValue(c.Id, out var stoppedAt) || stoppedAt >= c.End)).OrderBy(c => c.End))
                {
                    nutrition = Math.Min(1, Math.Max(0, nutrition - hunger * (meal.End - ateAt)) + meal.Gain);
                    ateAt = meal.End;
                }
                nutrition = Math.Max(0, nutrition - hunger * (choice.Start - ateAt));
                if (nutrition == 0 && pawn.SleepWhileStarving != Known(true))
                { findings.Add("sleep_requires_observed_food_recovery:" + choice.Id); return null; }
            }
            if (!continuing && (!pawn.SleepBlockedUntilTicks.TryGetValue(out var blocked) || choice.Start < blocked || level >= threshold))
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
    private static void NeedHorizon(double horizon)
    {
        if (!double.IsFinite(horizon) || horizon <= 0 || horizon > GameTime.TicksPerDay)
            throw new DomainException("needs horizon must be positive and at most one day");
    }
}
