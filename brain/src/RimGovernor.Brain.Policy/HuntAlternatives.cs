using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>A proposed hunt: the gunners who will be in the encounter and the prey group they are sent at. Never a minimum size.</summary>
public sealed record HuntRoster(EquatableArray<string> Gunners, EquatableArray<string> Prey)
{
    /// <summary>The estimate key: the same pawns and prey are the same encounter whatever order they were listed in.</summary>
    public string Key => string.Join('+', Gunners.Order(StringComparer.Ordinal)) + ">" + string.Join('+', Prey.Order(StringComparer.Ordinal));
}

/// <summary>
/// The prediction service's answer for one roster, reduced to counts over its independent samples. An observed fact
/// the runtime supplies; policy never calls the service. Counts are per sample: a sample is harmful when any
/// gunner suffers serious harm, deadly when any gunner dies, downed when every prey ends dead or downed.
/// </summary>
public sealed record HuntEstimate(int Samples, int HarmSamples, int DeathSamples,
    int DownedSamples, double ExpectedNutrition, double EncounterTicks);

/// <summary>One alternative the comparison saw. A refused alternative names why and is never ranked as zero risk.</summary>
public sealed record HuntAssessment(HuntRoster? Roster, string Refusal, HuntEstimate? Estimate, double LaborTicks)
{
    public bool Usable => Refusal.Length == 0;
}

public sealed record HuntSelection(HuntAssessment Selected, EquatableArray<HuntAssessment> Alternatives, EquatableArray<string> Findings);

public static class HuntAlternatives
{
    public const int MaxGunnerPool = 6, MaxTriplePool = 4, MaxTargets = 4, MaxRosters = 96;
    /// <summary>Herd-mates this close to a target are sent at together; a search aid for proposing targets, never a gate.</summary>
    private const int GroupRadius = 12;

    /// <summary>Whether <paramref name="other"/> is in <paramref name="prey"/>'s group: a herd-mate of the same kind within the group radius.</summary>
    public static bool Grouped(HuntActorRead other, HuntActorRead prey) =>
        other.Herd.Length != 0 && other.Herd == prey.Herd && other.Kind == prey.Kind
        && Math.Abs(other.Location.X - prey.Location.X) <= GroupRadius && Math.Abs(other.Location.Z - prey.Location.Z) <= GroupRadius;

    /// <summary>
    /// Bounded roster options: solos, pairs and a third gunner from the best-shooting available hunters, for each
    /// nearby prey group. No hunter is required; the no-hunt alternative is added by <see cref="Choose"/>.
    /// </summary>
    public static (EquatableArray<HuntRoster> Rosters, EquatableArray<string> Findings) Propose(HuntActivityRead read, IReadOnlySet<string> busy)
    {
        var findings = new SortedSet<string>(read.Findings, StringComparer.Ordinal);
        if (!read.Known) { findings.Add("hunt_observation_unknown"); return ([], EquatableArray.From(findings)); }
        var pool = read.Hunters.Where(h => h.Available && !busy.Contains(h.Pawn) && h.Weapon is not null && h.WeaponRange.IsKnown && h.WeaponRange.Value > 0)
            .OrderByDescending(h => h.Skills.FirstOrDefault(s => s.Skill == "Shooting")?.Level ?? 0).ThenBy(h => h.Pawn, StringComparer.Ordinal)
            .Take(MaxGunnerPool).ToArray();
        if (pool.Length == 0) findings.Add("no_available_gunner");
        var live = read.Prey.Where(p => !p.Downed && !p.Mental).ToArray();
        var groups = new List<string[]>();
        foreach (var prey in live)
        {
            var group = live.Where(o => o.Pawn == prey.Pawn || Grouped(o, prey)).Select(o => o.Pawn).Order(StringComparer.Ordinal).ToArray();
            if (!groups.Any(g => g.SequenceEqual(group, StringComparer.Ordinal))) groups.Add(group);
            if (groups.Count >= MaxTargets) { findings.Add("hunt_targets_truncated"); break; }
        }
        var rosters = new List<HuntRoster>();
        foreach (var group in groups)
        {
            IEnumerable<string[]> teams = pool.Select(h => new[] { h.Pawn })
                .Concat(pool.SelectMany((a, i) => pool.Skip(i + 1).Select(b => new[] { a.Pawn, b.Pawn })))
                .Concat(pool.Take(MaxTriplePool).SelectMany((a, i) => pool.Take(MaxTriplePool).Skip(i + 1).SelectMany((b, j) =>
                    pool.Take(MaxTriplePool).Skip(i + j + 2).Select(c => new[] { a.Pawn, b.Pawn, c.Pawn }))));
            foreach (var team in teams)
            {
                if (rosters.Count >= MaxRosters) { findings.Add("hunt_rosters_truncated"); break; }
                rosters.Add(new(EquatableArray.From(team.Order(StringComparer.Ordinal)), EquatableArray.From(group)));
            }
        }
        return (EquatableArray.From(rosters), EquatableArray.From(findings));
    }

    /// <summary>
    /// Reduces a prediction to a roster estimate. Anything but evidence with samples, a stated meat yield per prey, or
    /// a prediction for a different scene is a refusal, never zero risk.
    /// </summary>
    public static (HuntEstimate? Estimate, string Refusal) EstimateOf(HuntActivityRead read, HuntRoster roster, EncounterRequest request, EncounterPrediction prediction)
    {
        if (prediction.Status != EncounterStatus.Evidence) return (null, "prediction_" + prediction.Status.ToString().ToLowerInvariant() + (prediction.Reason.Length == 0 ? "" : ":" + prediction.Reason));
        if (prediction.Samples.Count == 0) return (null, "prediction_without_samples");
        // The request's actor ids are the observed pawn ids, so the estimate is for exactly this roster.
        var gunnerIds = request.Actors.Where(a => !a.Wild).Select(a => a.Id).ToArray();
        var wild = request.Actors.Where(a => a.Wild).ToArray();
        if (!gunnerIds.Order(StringComparer.Ordinal).SequenceEqual(roster.Gunners.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || !wild.Select(a => a.Id).Order(StringComparer.Ordinal).SequenceEqual(roster.Prey.Order(StringComparer.Ordinal), StringComparer.Ordinal)) return (null, "request_roster_mismatch");
        var yields = wild.ToDictionary(a => a.Id, a => read.Prey.FirstOrDefault(p => p.Pawn == a.Id), StringComparer.Ordinal);
        if (yields.Values.Any(p => p is null || !p.Meat.IsKnown || !p.MeatNutrition.IsKnown)) return (null, "prey_yield_unknown");
        var harm = 0; var death = 0; var downed = 0; var nutrition = 0.0;
        foreach (var sample in prediction.Samples)
        {
            var outcomes = sample.Actors.ToDictionary(a => a.Id, StringComparer.Ordinal);
            if (gunnerIds.Any(id => !outcomes.ContainsKey(id)) || wild.Any(a => !outcomes.ContainsKey(a.Id))) return (null, "sample_missing_actor");
            if (gunnerIds.Any(id => SeriousHarm(request.Actors.Single(a => a.Id == id), outcomes[id]))) harm++;
            if (gunnerIds.Any(id => outcomes[id].Dead)) death++;
            if (!sample.HorizonReached && wild.All(a => outcomes[a.Id] is { } o && (o.Dead || o.Downed))) downed++;
            foreach (var a in wild)
                if (outcomes[a.Id] is { } o && (o.Dead || o.Downed)) nutrition += yields[a.Id]!.Meat.Value * yields[a.Id]!.MeatNutrition.Value;
        }
        var ticks = prediction.Samples.Select(s => (double)s.DurationTicks).Order().ToArray();
        var n = prediction.Samples.Count;
        return (new(n, harm, death, downed, nutrition / n, ticks[ticks.Length / 2]), "");
    }

    /// <summary>Any new injury needing treatment, missing part, permanent injury, downing or death; an existing injury counts only if worsened.</summary>
    public static bool SeriousHarm(EncounterActor before, EncounterActorOutcome after) =>
        after.Dead || after.Downed || after.MissingParts.Except(before.MissingParts, StringComparer.Ordinal).Any()
        || after.Injuries.GroupBy(i => (i.Definition, i.Part)).Any(group =>
        {
            var baseline = before.Health.Where(h => h.Definition == group.Key.Definition && h.Part == group.Key.Part).ToArray();
            return group.Any(i => i.Tendable || i.Permanent || i.BleedRate > 0)
                && (group.Count() > baseline.Length || group.Sum(i => i.Severity) > baseline.Sum(h => h.Severity) + 0.0001);
        });

    /// <summary>
    /// Chooses among the proposed rosters and no hunt, in the epic's order: serious harm first (deaths, then any serious
    /// harm, each by its 95% Wilson upper bound so a small sample cannot look safer than it is), then the
    /// nutrition still missing after the chance prey is not downed, then total participant labor. A roster without a
    /// usable estimate or travel route is refused, not ranked. With no nutrition needed, or no usable roster, the
    /// choice is not to hunt.
    /// </summary>
    public static HuntSelection Choose(HuntActivityRead read, EquatableArray<HuntRoster> rosters, IReadOnlyDictionary<string, HuntEstimate> estimates,
        IReadOnlyList<CoupledRoute> routes, double nutritionNeeded)
    {
        var findings = new SortedSet<string> { "return_travel_and_retrieval_unassessed", "estimate_rates_carry_sampling_uncertainty" };
        var rows = new List<HuntAssessment>();
        foreach (var roster in rosters)
        {
            if (!estimates.TryGetValue(roster.Key, out var estimate)) { rows.Add(new(roster, "estimate_missing", null, 0)); continue; }
            var travel = new List<double>();
            var routed = true;
            foreach (var gunner in roster.Gunners)
            {
                var nearest = roster.Prey.Select(id => read.Prey.FirstOrDefault(p => p.Pawn == id)).Where(p => p is not null)
                    .Select(p => routes.FirstOrDefault(r => r.FromStart && r.Pawn == gunner && r.To == p!.Location)).Where(r => r is not null && r.Ticks.IsKnown)
                    .Select(r => r!.Ticks.Value).DefaultIfEmpty(double.NaN).Min();
                if (double.IsNaN(nearest)) { routed = false; break; }
                travel.Add(nearest);
            }
            if (!routed) { rows.Add(new(roster, "route_unknown", estimate, 0)); continue; }
            rows.Add(new(roster, "", estimate, travel.Sum() + estimate.EncounterTicks * roster.Gunners.Count));
        }
        var usable = rows.Where(r => r.Usable).OrderBy(r => r, Comparer<HuntAssessment>.Create((a, b) => Compare(a, b, nutritionNeeded))).ThenBy(r => r.Roster!.Key, StringComparer.Ordinal).ToArray();
        var none = new HuntAssessment(null, "", null, 0);
        if (nutritionNeeded <= 0) findings.Add("no_nutrition_needed");
        else if (usable.Length == 0) findings.Add("no_usable_hunt_roster");
        var selected = nutritionNeeded > 0 && usable.Length > 0 ? usable[0] : none;
        return new(selected, EquatableArray.From(usable.Concat(rows.Where(r => !r.Usable)).Append(none)), EquatableArray.From(findings));
    }

    private static int Compare(HuntAssessment a, HuntAssessment b, double needed)
    {
        var x = a.Estimate!; var y = b.Estimate!;
        var rank = WilsonUpper(x.DeathSamples, x.Samples).CompareTo(WilsonUpper(y.DeathSamples, y.Samples)); if (rank != 0) return rank;
        rank = WilsonUpper(x.HarmSamples, x.Samples).CompareTo(WilsonUpper(y.HarmSamples, y.Samples)); if (rank != 0) return rank;
        rank = Math.Max(0, needed - x.ExpectedNutrition).CompareTo(Math.Max(0, needed - y.ExpectedNutrition)); if (rank != 0) return rank;
        return a.LaborTicks.CompareTo(b.LaborTicks);
    }

    /// <summary>Upper end of the 95% Wilson interval for k events in n samples.</summary>
    public static double WilsonUpper(int k, int n)
    {
        if (n <= 0) return 1;
        const double z = 1.96;
        var p = (double)k / n;
        var denominator = 1 + z * z / n;
        return Math.Min(1, (p + z * z / (2 * n) + z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n))) / denominator);
    }
}
