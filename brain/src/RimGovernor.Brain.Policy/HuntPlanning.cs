using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What a food-plan review learned about hunting: the chosen alternative (when every proposed roster was answered),
/// the rosters still to ask the prediction service about and the read to build their scenes from (<see cref="Anchor"/>:
/// the comparison's read, whose prey are exactly those it compares; null when nothing is asked), and why a hunt is not offered.
/// </summary>
public sealed record HuntPlan(HuntSelection? Selection, EquatableArray<HuntRoster> Unestimated, HuntActivityRead? Anchor, EquatableArray<string> Findings)
{
    /// <summary>The prey of the rosters still being asked about: a lone hunt must not designate them before the comparison is answered.</summary>
    public IReadOnlySet<string> Awaiting => Unestimated.SelectMany(r => r.Prey).ToHashSet(StringComparer.Ordinal);

    /// <summary>The hunt formation channel to offer the food plan, or none: no selection, no roster chosen or a no-hunt choice.</summary>
    public SupplyCandidate? Channel(IReadOnlyList<AcquisitionSource> sources) =>
        Selection is { Selected: { Roster: { } roster } selected } ? HuntPlanningRules.HuntRosterChannel(selected, roster, sources) : null;
}

public static class HuntPlanningRules
{
    /// <summary>
    /// Compares the hunt alternatives for the observed colony in two stages. Rosters without an answer yet (neither an
    /// estimate nor a refusal) are returned to be asked and the hunt waits for them: a half-answered comparison would
    /// pick the first roster to come back, not the best. Once the plain rosters are answered, the best of them are
    /// restated with a gunner-retriever leaving at the encounter ticks the first stage measured, asked the same way,
    /// and the choice is made over both stages.
    /// <para>
    /// The comparison is anchored to one read (<see cref="HuntEstimateView.Anchor"/>: the read it was first asked from,
    /// with the prey the plan could hunt then): every scene of both stages is built from it, whatever the later rounds
    /// read, and its answers are gathered without regard to how far the pawns drift meanwhile. The service answers
    /// scenes one at a time, so judging each answer against the newest read staled the early ones before the late ones
    /// arrived (#3525). While it stands, the comparison compares the prey the anchor saw: an animal that wandered into
    /// the window since is proposed by the next comparison, after a choice or a restart, never by this one. The anchor
    /// is judged two ways. Every round, its prey (<see cref="HuntEncounterRequests.PreyDrift"/>: one gone from the read,
    /// downed, in a mental state or no longer huntable, or a group whose members stand grouped otherwise; never where
    /// an animal stands) and whether it saw every proposed gunner. When a stage is fully answered, at choice time, its
    /// gunners (<see cref="HuntEncounterRequests.StillAnchored"/>: each rostered gunner within
    /// <see cref="HuntEncounterRequests.MoveTolerance"/> of where the anchor saw it and otherwise the gunner it saw, in
    /// the same scene and weather). Either way the comparison then starts over from the current read
    /// (<c>hunt_anchor_stale</c>, with <c>hunt_anchor_drift:</c> naming what drifted). Rosters, labor and the choice
    /// itself always come from the current read and its routes.
    /// </para>
    /// </summary>
    public static HuntPlan PlanHunt(HuntActivityRead? read, IReadOnlyList<AcquisitionSource> sources, IReadOnlyList<CoupledRoute> routes, HuntEstimateView view, double nutritionNeeded)
    {
        if (read is null || !read.Known) return new(null, [], null, ["hunt_observation_unknown"]);
        if (nutritionNeeded <= 0) return new(null, [], null, ["no_nutrition_needed"]);
        // Only prey the food plan may send a hunt at: not designated or taken, still standing and food-yielding.
        var allowed = sources.Where(s => s.SquadPrey() && s.Food && !s.Downed).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var observed = read with { Prey = EquatableArray.From(read.Prey.Where(p => allowed.Contains(p.Pawn) && !p.Downed && !p.Mental)) };
        var busy = new HashSet<string>(StringComparer.Ordinal);
        if (view.Anchor is not { } anchor) return Asked(observed, HuntAlternatives.Propose(observed, busy, routes), []);
        var seen = anchor.Prey.Select(p => p.Pawn).ToHashSet(StringComparer.Ordinal);
        var (rosters, findings) = HuntAlternatives.Propose(observed with { Prey = EquatableArray.From(observed.Prey.Where(p => seen.Contains(p.Pawn))) }, busy, routes);
        // The restart names what drifted from the anchor, so a comparison that never completes says why.
        HuntPlan Restarted(IEnumerable<string> drift) =>
            Asked(observed, HuntAlternatives.Propose(observed, busy, routes), ["hunt_anchor_stale", "hunt_anchor_drift:" + string.Join(",", drift)]);
        var preyDrift = HuntEncounterRequests.PreyDrift(anchor, read, allowed).ToList();
        if (preyDrift.Count != 0) return Restarted(preyDrift);
        if (!HuntEncounterRequests.StatesGunners(anchor, rosters)) return Restarted(HuntEncounterRequests.GunnerDrift(anchor, read, rosters));
        if (rosters.Count == 0) return new(null, [], null, findings);
        var pending = EquatableArray.From(findings.Append("hunt_estimates_pending"));
        var unanswered = Unanswered(rosters, view);
        if (unanswered.Count != 0) return new(null, unanswered, anchor, pending);
        if (!HuntEncounterRequests.StillAnchored(anchor, read, rosters)) return Restarted(HuntEncounterRequests.GunnerDrift(anchor, read, rosters));
        var first = HuntAlternatives.Choose(read, rosters, view.Estimates, routes, nutritionNeeded);
        var second = HuntAlternatives.ProposeRetrieverStage(read, first);
        unanswered = Unanswered(second, view);
        if (unanswered.Count != 0) return new(null, unanswered, anchor, pending);
        var selection = second.Count == 0 ? first : HuntAlternatives.Choose(read, EquatableArray.From(rosters.Concat(second)), view.Estimates, routes, nutritionNeeded);
        return new(selection, [], null, EquatableArray.From(findings.Concat(selection.Findings).Distinct(StringComparer.Ordinal)));
    }

    /// <summary>A new comparison from <paramref name="observed"/>: every proposed scene is to be asked with it as the anchor; with nothing to propose there is no comparison and no anchor.</summary>
    private static HuntPlan Asked(HuntActivityRead observed, (EquatableArray<HuntRoster> Rosters, EquatableArray<string> Findings) proposed, IEnumerable<string> why) =>
        proposed.Rosters.Count == 0
            ? new(null, [], null, EquatableArray.From(proposed.Findings.Concat(why)))
            : new(null, Unanswered(proposed.Rosters, HuntEstimateView.None), observed, EquatableArray.From(proposed.Findings.Concat(why).Append("hunt_estimates_pending")));

    /// <summary>The rosters whose scene has neither an estimate nor a refusal, one per scene: a hauler's roster shares its gunners' scene.</summary>
    private static EquatableArray<HuntRoster> Unanswered(IEnumerable<HuntRoster> rosters, HuntEstimateView answers) =>
        EquatableArray.From(rosters.Where(r => !answers.Estimates.ContainsKey(r.SceneKey) && !answers.Refusals.ContainsKey(r.SceneKey)).DistinctBy(r => r.SceneKey, StringComparer.Ordinal));

    /// <summary>
    /// The formation channel of the chosen roster: its estimated meat (already net of the samples in which the prey was
    /// not downed) and the products of the prey it downed, the participants' labor (with its return and haul parts as
    /// terms), and the harm rate its samples showed as the risk that discounts the yield. The roster's gunners, retriever
    /// and prey ride on the channel, so execution sends exactly the gunners and holds the retriever for its trip.
    /// </summary>
    public static SupplyCandidate HuntRosterChannel(HuntAssessment selected, HuntRoster roster, IReadOnlyList<AcquisitionSource> sources)
    {
        var estimate = selected.Estimate!;
        var prey = roster.Prey.Select(id => sources.FirstOrDefault(s => s.Id == id)).Where(s => s is not null).Select(s => s!).ToArray();
        var downed = estimate.Samples == 0 ? 0 : (double)estimate.DownedSamples / estimate.Samples;
        var c = CandidateRules.FoodCandidate(CandidateKind.Hunt, "roster:" + roster.Key, Unknown<double>());
        var yields = new List<CandidateYield>(c.Yields);
        yields[0] = yields[0] with
        {
            PerDay = Known(estimate.ExpectedNutrition / FoodPlanRules.FoodHuntCycleDays),
            StockCap = Known((long)Math.Ceiling(estimate.ExpectedNutrition)),
        };
        var products = new Dictionary<Resource, double>();
        foreach (var p in prey.SelectMany(s => s.Products))
        {
            products[p.Def] = products.GetValueOrDefault(p.Def) + (p.Amount * downed);
        }
        foreach (var (def, amount) in products.OrderBy(p => p.Key.Value, StringComparer.Ordinal))
        {
            yields.Add(new CandidateYield { Good = new ResourceKey { Def = def }, PerDay = Known(amount / FoodPlanRules.FoodHuntCycleDays) });
        }
        var harmRate = estimate.Samples == 0 ? 1 : (double)estimate.HarmSamples / estimate.Samples;
        return c with
        {
            LeadDays = Known(0.0),
            Source = HuntCreditRules.HuntSource,
            State = CandidateRules.FoodState(Known(false), Known(prey.Length > 0 && prey.All(s => s.Designated))),
            Prey = EquatableArray.From(roster.Prey.Order(StringComparer.Ordinal)),
            Hunters = EquatableArray.From(roster.Gunners.Order(StringComparer.Ordinal)),
            Retrievers = EquatableArray.From(roster.Retrievers.Order(StringComparer.Ordinal)),
            Yields = [.. yields],
            LaborPerDay = Known(selected.LaborTicks / FoodPlanRules.FoodHuntCycleDays),
            Risk = [new CandidateRisk(CandidateRiskKind.Revenge, Math.Min(1, harmRate))],
            Terms =
            [
                new("estimated_cycle_days", FoodPlanRules.FoodHuntCycleDays),
                new("estimated_work_ticks", selected.LaborTicks),
                new("hunt_return_ticks", selected.ReturnTicks),
                new("hunt_haul_ticks", selected.HaulTicks),
                new("prey", roster.Prey.Count),
                new("hunters", roster.Gunners.Count),
                new("retrievers", roster.Retrievers.Count),
                new("hunt_samples", estimate.Samples),
                new("hunt_harm_samples", estimate.HarmSamples),
                new("hunt_death_samples", estimate.DeathSamples),
                new("hunt_downed_samples", estimate.DownedSamples),
                new("hunt_death_upper_bound", HuntAlternatives.WilsonUpper(estimate.DeathSamples, estimate.Samples)),
                new("hunt_harm_upper_bound", HuntAlternatives.WilsonUpper(estimate.HarmSamples, estimate.Samples)),
            ],
        };
    }

    /// <summary>The selected hunters of the open hunt channels, sorted: who the executed formation may send.</summary>
    public static IReadOnlyList<PawnId> HuntRequestHunters(Fact<FoodPlan> plan) => OpenHuntPawns(plan, c => c.Hunters);

    /// <summary>The retrievers of the open hunt channels, sorted: not drafted, but held for their trip, so no other voluntary work takes them.</summary>
    public static IReadOnlyList<PawnId> HuntRequestRetrievers(Fact<FoodPlan> plan) => OpenHuntPawns(plan, c => c.Retrievers);

    private static List<PawnId> OpenHuntPawns(Fact<FoodPlan> plan, Func<SupplyCandidate, EquatableArray<string>> role)
    {
        if (!plan.TryGetValue(out var p))
        {
            return [];
        }
        var output = new List<PawnId>();
        foreach (var e in p.Portfolio)
        {
            if (e.Channel.Kind == CandidateKind.Hunt && e.Decision == FoodPlanDecision.Open)
            {
                output.AddRange(role(e.Channel).Select(id => new PawnId(id)));
            }
        }
        output.Sort();
        return output;
    }
}
