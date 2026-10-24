using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>What a food-plan review learned about hunting: the chosen alternative, and why a hunt is not offered.</summary>
public sealed record HuntPlan(HuntSelection? Selection, EquatableArray<string> Findings)
{
    /// <summary>The hunt formation channel to offer the food plan, or none: no selection, no roster chosen or a no-hunt choice.</summary>
    public SupplyCandidate? Channel(IReadOnlyList<AcquisitionSource> sources) =>
        Selection is { Selected: { Roster: { } roster } selected } ? HuntPlanningRules.HuntRosterChannel(selected, roster, sources) : null;
}

public static class HuntPlanningRules
{
    /// <summary>
    /// Compares the hunt alternatives for the observed colony in two stages, each estimated analytically in the review
    /// (<see cref="HuntEstimator"/>). The plain rosters are chosen among first; the best of them are then restated with a
    /// gunner-retriever leaving at the encounter ticks the first stage estimated, and the choice is made over both
    /// stages. Rosters, labor and the choice all come from the current read and its routes. A colonist in
    /// <paramref name="busy"/> (one whose hunger takes him off a hunt) is no gunner or hauler of any roster.
    /// </summary>
    public static HuntPlan PlanHunt(HuntActivityRead? read, IReadOnlyList<AcquisitionSource> sources, IReadOnlyList<CoupledRoute> routes, double nutritionNeeded, IReadOnlySet<string> busy) =>
        PlanHunt(read, sources, routes, nutritionNeeded, HuntEstimator.Estimate, busy);

    /// <summary><see cref="PlanHunt(HuntActivityRead?, IReadOnlyList{AcquisitionSource}, IReadOnlyList{CoupledRoute}, double, IReadOnlySet{string})"/> with the estimate stated per roster over the filtered read.</summary>
    internal static HuntPlan PlanHunt(HuntActivityRead? read, IReadOnlyList<AcquisitionSource> sources, IReadOnlyList<CoupledRoute> routes, double nutritionNeeded,
        Func<HuntActivityRead, HuntRoster, (HuntEstimate? Estimate, string Refusal)> estimateOf, IReadOnlySet<string>? busy = null)
    {
        if (read is null) return new(null, ["hunt_observation_unknown"]);
        if (nutritionNeeded <= 0) return new(null, ["no_nutrition_needed"]);
        // Only prey the food plan may send a hunt at: not designated or taken, still standing and food-yielding.
        var allowed = sources.Where(s => s.SquadPrey() && s.Food && !s.Downed).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var observed = read with { Prey = EquatableArray.From(read.Prey.Where(p => allowed.Contains(p.Pawn) && !p.Downed && !p.Mental)) };
        var (rosters, findings) = HuntAlternatives.Propose(observed, busy ?? new HashSet<string>(StringComparer.Ordinal), routes);
        if (rosters.Count == 0) return new(null, findings);
        var first = HuntAlternatives.Choose(observed, rosters, r => estimateOf(observed, r), routes, nutritionNeeded);
        var second = HuntAlternatives.ProposeRetrieverStage(observed, first);
        var selection = second.Count == 0 ? first : HuntAlternatives.Choose(observed, EquatableArray.From(rosters.Concat(second)), r => estimateOf(observed, r), routes, nutritionNeeded);
        return new(selection, EquatableArray.From(findings.Concat(selection.Findings).Distinct(StringComparer.Ordinal)));
    }

    /// <summary>
    /// The colonists whose hunger takes them off a hunt: at the hunger the clock holds for
    /// (<see cref="HazardThresholdsRules.HungerHoldCategory"/>) or past it. A drafted gunner does not eat, so a hunt that
    /// keeps him starves him; a pawn without a food need is not counted.
    /// </summary>
    public static IReadOnlySet<string> Hungry(IEnumerable<WorkPawn> pawns) =>
        pawns.Where(p => p.Food is FoodNeed.Has { Hunger: var hunger } && hunger >= HazardThresholdsRules.HungerHoldCategory).Select(p => p.Id.Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The formation channel of the chosen roster: its estimated meat (already net of the chance the prey is not
    /// downed) and the products of the prey it downs, the participants' labor (with its return and haul parts as
    /// terms), and the estimated harm chance as the risk that discounts the yield. The roster's gunners, retriever
    /// and prey ride on the channel, so execution sends exactly the gunners and holds the retriever for its trip.
    /// </summary>
    public static SupplyCandidate HuntRosterChannel(HuntAssessment selected, HuntRoster roster, IReadOnlyList<AcquisitionSource> sources)
    {
        var estimate = selected.Estimate!;
        var prey = roster.Prey.Select(id => sources.FirstOrDefault(s => s.Id == id)).Where(s => s is not null).Select(s => s!).ToArray();
        var downed = estimate.DownedChance;
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
            Risk = [new CandidateRisk(CandidateRiskKind.Revenge, Math.Clamp(estimate.HarmChance, 0, 1))],
            Terms =
            [
                new("estimated_cycle_days", FoodPlanRules.FoodHuntCycleDays),
                new("estimated_work_ticks", selected.LaborTicks),
                new("hunt_return_ticks", selected.ReturnTicks),
                new("hunt_haul_ticks", selected.HaulTicks),
                new("prey", roster.Prey.Count),
                new("hunters", roster.Gunners.Count),
                new("retrievers", roster.Retrievers.Count),
                new("hunt_risk_estimated", estimate.RiskEstimated ? 1 : 0),
                new("hunt_harm_chance", estimate.HarmChance),
                new("hunt_death_chance", estimate.DeathChance),
                new("hunt_downed_chance", estimate.DownedChance),
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
