using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// A proposed hunt: the gunners who will be in the encounter, the prey group they are sent at, and who takes the
/// corpses (<see cref="Retrievers"/>). Never a minimum size.
/// </summary>
public sealed record HuntRoster(EquatableArray<string> Gunners, EquatableArray<string> Prey)
{
    /// <summary>
    /// Who hauls the kills, at most one: empty, the gunners carry their own; a colonist outside the encounter who walks
    /// to the prey after it and hauls (a hauler); or one of the gunners, who leaves the encounter at
    /// <see cref="DepartureTick"/> to haul (a gunner-retriever).
    /// </summary>
    public EquatableArray<string> Retrievers { get; init; }

    /// <summary>The encounter tick a gunner-retriever leaves at: the plain roster's median encounter ticks. 0 without one.</summary>
    public int DepartureTick { get; init; }

    public string? Retriever => Retrievers.Count > 0 ? Retrievers[0] : null;

    public bool GunnerRetriever => Retriever is { } r && Gunners.Contains(r);

    /// <summary>
    /// The scene the predictor is asked about: the same pawns and prey are the same encounter whatever order they were
    /// listed in, plus a gunner-retriever's departure. A hauler is outside the encounter, so its roster shares the
    /// plain roster's scene and estimate.
    /// </summary>
    public string SceneKey => string.Join('+', Gunners.Order(StringComparer.Ordinal)) + ">" + string.Join('+', Prey.Order(StringComparer.Ordinal))
        + (GunnerRetriever ? "<" + Retriever + "@" + DepartureTick : "");

    /// <summary>The roster's identity: its scene and, for a hauler, who hauls.</summary>
    public string Key => SceneKey + (Retriever is { } r && !GunnerRetriever ? "<" + r : "");
}

/// <summary>
/// The prediction service's answer for one roster, reduced to counts over its independent samples. An observed fact
/// the runtime supplies; policy never calls the service. Counts are per sample: a sample is harmful when any
/// gunner suffers serious harm, deadly when any gunner dies, downed when every prey ends dead or downed.
/// </summary>
public sealed record HuntEstimate(int Samples, int HarmSamples, int DeathSamples,
    int DownedSamples, double ExpectedNutrition, double EncounterTicks);

/// <summary>
/// One alternative the comparison saw. A refused alternative names why and is never ranked as zero risk. LaborTicks is
/// every participant's time: approach, encounter, the empty returns (<see cref="ReturnTicks"/>) and the corpse hauls
/// with a hauler's walk (<see cref="HaulTicks"/>).
/// </summary>
public sealed record HuntAssessment(HuntRoster? Roster, string Refusal, HuntEstimate? Estimate, double LaborTicks, double ReturnTicks, double HaulTicks)
{
    public bool Usable => Refusal.Length == 0;
}

public sealed record HuntSelection(HuntAssessment Selected, EquatableArray<HuntAssessment> Alternatives, EquatableArray<string> Findings);

public static class HuntAlternatives
{
    public const int MaxGunnerPool = 6, MaxTriplePool = 4, MaxTargets = 4, MaxRosters = 96;
    /// <summary>Hauler candidates per prey group, and second-stage gunner-retriever rosters per prey group.</summary>
    public const int MaxHaulers = 2, MaxRetrieverStage = 3;
    /// <summary>Herd-mates this close to a target are sent at together; a search aid for proposing targets, never a gate.</summary>
    private const int GroupRadius = 12;

    /// <summary>Whether <paramref name="other"/> is in <paramref name="prey"/>'s group: a herd-mate of the same kind within the group radius.</summary>
    public static bool Grouped(HuntActorRead other, HuntActorRead prey) =>
        other.Herd.Length != 0 && other.Herd == prey.Herd && other.Kind == prey.Kind
        && Math.Abs(other.Location.X - prey.Location.X) <= GroupRadius && Math.Abs(other.Location.Z - prey.Location.Z) <= GroupRadius;

    /// <summary>
    /// Bounded roster options: solos, pairs and a third gunner from the best-shooting available hunters, for each
    /// nearby prey group, each also with a hauler (an available colonist outside the gunner pool whose hauling is
    /// active, the <see cref="MaxHaulers"/> fastest by their walk and haul trips) when there is one. No hunter is
    /// required; the no-hunt alternative is added by <see cref="Choose"/>. <see cref="MaxRosters"/> bounds the scenes
    /// asked of the predictor; a hauler's roster shares its gunners' scene, so it adds none.
    /// </summary>
    public static (EquatableArray<HuntRoster> Rosters, EquatableArray<string> Findings) Propose(HuntActivityRead read, IReadOnlySet<string> busy, IReadOnlyList<CoupledRoute> routes)
    {
        var findings = new SortedSet<string>(read.Findings, StringComparer.Ordinal);
        if (!read.Known) { findings.Add("hunt_observation_unknown"); return ([], EquatableArray.From(findings)); }
        var pool = read.Hunters.Where(h => h.Available && !busy.Contains(h.Pawn) && h.Weapon is not null && h.WeaponRange.IsKnown && h.WeaponRange.Value > 0)
            .OrderByDescending(h => Shooting(h)).ThenBy(h => h.Pawn, StringComparer.Ordinal)
            .Take(MaxGunnerPool).ToArray();
        if (pool.Length == 0) findings.Add("no_available_gunner");
        var poolIds = pool.Select(h => h.Pawn).ToHashSet(StringComparer.Ordinal);
        var haulerPool = read.Hunters.Where(h => h.Available && h.Hauls && !busy.Contains(h.Pawn) && !poolIds.Contains(h.Pawn)).ToArray();
        var live = read.Prey.Where(p => !p.Downed && !p.Mental).ToArray();
        var groups = new List<HuntActorRead[]>();
        foreach (var prey in live)
        {
            var group = live.Where(o => o.Pawn == prey.Pawn || Grouped(o, prey)).OrderBy(o => o.Pawn, StringComparer.Ordinal).ToArray();
            if (!groups.Any(g => g.Select(o => o.Pawn).SequenceEqual(group.Select(o => o.Pawn), StringComparer.Ordinal))) groups.Add(group);
            if (groups.Count >= MaxTargets) { findings.Add("hunt_targets_truncated"); break; }
        }
        var rosters = new List<HuntRoster>();
        var scenes = 0;
        foreach (var group in groups)
        {
            var haulers = haulerPool.Select(h => (h.Pawn, Ticks: HaulerTrip(h.Pawn, group, routes))).Where(h => h.Ticks is not null)
                .OrderBy(h => h.Ticks).ThenBy(h => h.Pawn, StringComparer.Ordinal).Take(MaxHaulers).Select(h => h.Pawn).ToArray();
            IEnumerable<string[]> teams = pool.Select(h => new[] { h.Pawn })
                .Concat(pool.SelectMany((a, i) => pool.Skip(i + 1).Select(b => new[] { a.Pawn, b.Pawn })))
                .Concat(pool.Take(MaxTriplePool).SelectMany((a, i) => pool.Take(MaxTriplePool).Skip(i + 1).SelectMany((b, j) =>
                    pool.Take(MaxTriplePool).Skip(i + j + 2).Select(c => new[] { a.Pawn, b.Pawn, c.Pawn }))));
            foreach (var team in teams)
            {
                if (scenes >= MaxRosters) { findings.Add("hunt_rosters_truncated"); break; }
                scenes++;
                var roster = new HuntRoster(EquatableArray.From(team.Order(StringComparer.Ordinal)), EquatableArray.From(group.Select(o => o.Pawn)));
                rosters.Add(roster);
                rosters.AddRange(haulers.Select(h => roster with { Retrievers = [h] }));
            }
        }
        return (EquatableArray.From(rosters), EquatableArray.From(findings));
    }

    /// <summary>
    /// The second stage, once the plain rosters are answered: the <see cref="MaxRetrieverStage"/> best-ranked usable
    /// plain rosters of each prey group with two or more gunners, each again with its least-skilled shooter leaving the
    /// encounter at the roster's median encounter ticks to haul. A lone gunner leaving would only stop shooting, so solos
    /// are not restated; its own haul is the plain roster's.
    /// </summary>
    public static EquatableArray<HuntRoster> ProposeRetrieverStage(HuntActivityRead read, HuntSelection first)
    {
        var output = new List<HuntRoster>();
        foreach (var group in first.Alternatives.Where(a => a.Usable && a.Roster is { Retrievers.Count: 0, Gunners.Count: >= 2 }).GroupBy(a => a.Roster!.Prey))
        {
            foreach (var a in group.Take(MaxRetrieverStage))
            {
                var roster = a.Roster!;
                var tick = (int)Math.Round(a.Estimate!.EncounterTicks);
                if (tick <= 0) continue;
                var leaver = roster.Gunners.OrderBy(g => Shooting(read.Hunters.FirstOrDefault(h => h.Pawn == g))).ThenBy(g => g, StringComparer.Ordinal).First();
                output.Add(roster with { Retrievers = [leaver], DepartureTick = tick });
            }
        }
        return EquatableArray.From(output);
    }

    private static int Shooting(HuntActorRead? hunter) => hunter?.Skills.FirstOrDefault(s => s.Skill == "Shooting")?.Level ?? 0;

    /// <summary>A hauler's trip for the group: its walk to the nearest prey, then one load per corpse to the corpse store; null where any leg is unknown.</summary>
    private static double? HaulerTrip(string pawn, IReadOnlyList<HuntActorRead> group, IReadOnlyList<CoupledRoute> routes)
    {
        var walk = group.Select(p => Walk(routes, pawn, p.Location)).Where(t => t is not null).Min();
        if (walk is null) return null;
        var loads = 0.0;
        foreach (var p in group)
        {
            if (Leg(routes, pawn, p.Location, p.CorpseStore) is not { } load) return null;
            loads += load;
        }
        return walk + loads;
    }

    private static double? Walk(IReadOnlyList<CoupledRoute> routes, string pawn, Cell to) =>
        routes.FirstOrDefault(r => r.FromStart && r.Pawn == pawn && r.To == to) is { Ticks.IsKnown: true } r ? r.Ticks.Value : null;

    private static double? Leg(IReadOnlyList<CoupledRoute> routes, string pawn, Cell from, Cell? to) =>
        to is { } t && routes.FirstOrDefault(r => !r.FromStart && r.Pawn == pawn && r.From == from && r.To == t) is { Ticks.IsKnown: true } r ? r.Ticks.Value : null;

    /// <summary>
    /// Reduces a prediction to a roster estimate. Anything but evidence with samples, a stated meat yield per prey, or
    /// a prediction for a different scene is a refusal, never zero risk.
    /// </summary>
    public static (HuntEstimate? Estimate, string Refusal) EstimateOf(HuntActivityRead read, HuntRoster roster, EncounterRequest request, EncounterPrediction prediction)
    {
        if (prediction.Status != EncounterStatus.Evidence) return (null, "prediction_" + prediction.Status.ToString().ToLowerInvariant() + (prediction.Reason.Length == 0 ? "" : ":" + prediction.Reason));
        if (prediction.Samples.Count == 0) return (null, "prediction_without_samples");
        // The request's actor ids are the observed pawn ids, so the estimate is for exactly this roster, its departure included.
        var gunnerIds = request.Actors.Where(a => !a.Wild).Select(a => a.Id).ToArray();
        var wild = request.Actors.Where(a => a.Wild).ToArray();
        if (!gunnerIds.Order(StringComparer.Ordinal).SequenceEqual(roster.Gunners.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || !wild.Select(a => a.Id).Order(StringComparer.Ordinal).SequenceEqual(roster.Prey.Order(StringComparer.Ordinal), StringComparer.Ordinal)) return (null, "request_roster_mismatch");
        var leavers = request.Actors.Where(a => !a.Wild && a.Departure is not null).ToArray();
        var leaver = roster.GunnerRetriever ? roster.Retriever : null;
        if (leavers.Length > 1 || (leavers.Length == 1) != (leaver is not null)
            || leavers.Length == 1 && (leavers[0].Id != leaver || leavers[0].Departure!.Tick != roster.DepartureTick)) return (null, "request_roster_mismatch");
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
    /// nutrition still missing after the chance prey is not downed, then total participant labor (<see cref="Labor"/>).
    /// A roster without a usable estimate, an approach route or a return route is refused, not ranked. With no
    /// nutrition needed, or no usable roster, the choice is not to hunt.
    /// </summary>
    public static HuntSelection Choose(HuntActivityRead read, EquatableArray<HuntRoster> rosters, IReadOnlyDictionary<string, HuntEstimate> estimates,
        IReadOnlyList<CoupledRoute> routes, double nutritionNeeded)
    {
        var findings = new SortedSet<string> { "estimate_rates_carry_sampling_uncertainty" };
        var rows = new List<HuntAssessment>();
        foreach (var roster in rosters)
        {
            if (!estimates.TryGetValue(roster.SceneKey, out var estimate)) { rows.Add(new(roster, "estimate_missing", null, 0, 0, 0)); continue; }
            var (labor, refusal) = Labor(read, roster, estimate, routes);
            rows.Add(labor is { } l ? new(roster, "", estimate, l.Total, l.Return, l.Haul) : new(roster, refusal, estimate, 0, 0, 0));
        }
        var usable = rows.Where(r => r.Usable).OrderBy(r => r, Comparer<HuntAssessment>.Create((a, b) => Compare(a, b, nutritionNeeded))).ThenBy(r => r.Roster!.Key, StringComparer.Ordinal).ToArray();
        var none = new HuntAssessment(null, "", null, 0, 0, 0);
        if (nutritionNeeded <= 0) findings.Add("no_nutrition_needed");
        else if (usable.Length == 0) findings.Add("no_usable_hunt_roster");
        var selected = nutritionNeeded > 0 && usable.Length > 0 ? usable[0] : none;
        return new(selected, EquatableArray.From(usable.Concat(rows.Where(r => !r.Usable)).Append(none)), EquatableArray.From(findings));
    }

    /// <summary>
    /// The roster's participant labor from the coupled routes. Each gunner walks to its nearest prey and stays the
    /// encounter's median ticks (a gunner-retriever until it leaves). Then one load per corpse goes to the corpse store
    /// native would haul it to: by the retriever (a hauler first walks to the prey), else by the gunner whose trip is
    /// shortest. A gunner who carries nothing walks back empty to where it stood. A missing approach is
    /// <c>route_unknown</c>; a missing haul, walk or return is <c>return_route_unknown</c>; neither is priced at zero.
    /// </summary>
    private static ((double Total, double Return, double Haul)? Labor, string Refusal) Labor(HuntActivityRead read, HuntRoster roster, HuntEstimate estimate, IReadOnlyList<CoupledRoute> routes)
    {
        var prey = roster.Prey.Select(id => read.Prey.FirstOrDefault(p => p.Pawn == id)).Where(p => p is not null).Select(p => p!).ToArray();
        if (prey.Length == 0) return (null, "prey_unobserved");
        var hunters = roster.Gunners.Concat(roster.Retrievers).Distinct(StringComparer.Ordinal).ToDictionary(id => id, id => read.Hunters.FirstOrDefault(h => h.Pawn == id), StringComparer.Ordinal);
        if (hunters.Values.Any(h => h is null)) return (null, "hunter_unobserved");
        var nearest = new Dictionary<string, HuntActorRead>(StringComparer.Ordinal);
        var approach = 0.0; var haul = 0.0;
        foreach (var id in hunters.Keys)
        {
            var best = prey.Select(p => (Prey: p, Ticks: Walk(routes, id, p.Location))).Where(x => x.Ticks is not null).OrderBy(x => x.Ticks).ThenBy(x => x.Prey.Pawn, StringComparer.Ordinal).FirstOrDefault();
            if (best.Prey is null) return (null, roster.Gunners.Contains(id) ? "route_unknown" : "return_route_unknown");
            nearest[id] = best.Prey;
            if (roster.Gunners.Contains(id)) approach += best.Ticks!.Value; else haul += best.Ticks!.Value;
        }
        string[] carriers = roster.Retriever is { } retriever ? [retriever] : [.. roster.Gunners];
        var hauled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in prey)
        {
            var trip = carriers.Select(c => (Carrier: c, Ticks: Leg(routes, c, p.Location, p.CorpseStore))).Where(x => x.Ticks is not null).OrderBy(x => x.Ticks).ThenBy(x => x.Carrier, StringComparer.Ordinal).FirstOrDefault();
            if (trip.Carrier is null) return (null, "return_route_unknown");
            haul += trip.Ticks!.Value;
            hauled.Add(trip.Carrier);
        }
        var back = 0.0;
        foreach (var g in roster.Gunners.Where(g => !hauled.Contains(g)))
        {
            if (Leg(routes, g, nearest[g].Location, hunters[g]!.Location) is not { } empty) return (null, "return_route_unknown");
            back += empty;
        }
        var encounter = roster.Gunners.Sum(g => roster.GunnerRetriever && g == roster.Retriever ? Math.Min(roster.DepartureTick, estimate.EncounterTicks) : estimate.EncounterTicks);
        return ((approach + encounter + back + haul, back, haul), "");
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
