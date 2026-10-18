using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What a food-plan review learned about hunting: the chosen alternative (when every proposed roster was answered),
/// the rosters still to ask the prediction service about, and why a hunt is not offered.
/// </summary>
public sealed record HuntPlan(HuntSelection? Selection, EquatableArray<HuntRoster> Unestimated, EquatableArray<string> Findings)
{
    public static readonly HuntPlan None = new(null, [], []);

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
    /// </summary>
    public static HuntPlan PlanHunt(HuntActivityRead? read, IReadOnlyList<AcquisitionSource> sources, IReadOnlyList<CoupledRoute> routes, HuntEstimateView view, double nutritionNeeded)
    {
        if (read is null || !read.Known) return new(null, [], ["hunt_observation_unknown"]);
        if (nutritionNeeded <= 0) return new(null, [], ["no_nutrition_needed"]);
        // Only prey the food plan may send a hunt at: not designated or taken, still standing and food-yielding.
        var allowed = sources.Where(s => s.SquadPrey() && s.Food && !s.Downed).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var (rosters, findings) = HuntAlternatives.Propose(read with { Prey = EquatableArray.From(read.Prey.Where(p => allowed.Contains(p.Pawn))) }, new HashSet<string>(StringComparer.Ordinal), routes);
        if (rosters.Count == 0) return new(null, [], findings);
        var unanswered = Unanswered(rosters, view);
        if (unanswered.Count != 0) return new(null, unanswered, EquatableArray.From(findings.Append("hunt_estimates_pending")));
        var first = HuntAlternatives.Choose(read, rosters, view.Estimates, routes, nutritionNeeded);
        var second = HuntAlternatives.ProposeRetrieverStage(read, first);
        unanswered = Unanswered(second, view);
        if (unanswered.Count != 0) return new(null, unanswered, EquatableArray.From(findings.Append("hunt_estimates_pending")));
        var selection = second.Count == 0 ? first : HuntAlternatives.Choose(read, EquatableArray.From(rosters.Concat(second)), view.Estimates, routes, nutritionNeeded);
        return new(selection, [], EquatableArray.From(findings.Concat(selection.Findings).Distinct(StringComparer.Ordinal)));
    }

    /// <summary>The rosters whose scene has neither an estimate nor a refusal, one per scene: a hauler's roster shares its gunners' scene.</summary>
    private static EquatableArray<HuntRoster> Unanswered(IEnumerable<HuntRoster> rosters, HuntEstimateView view) =>
        EquatableArray.From(rosters.Where(r => !view.Estimates.ContainsKey(r.SceneKey) && !view.Refusals.ContainsKey(r.SceneKey)).DistinctBy(r => r.SceneKey, StringComparer.Ordinal));

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
