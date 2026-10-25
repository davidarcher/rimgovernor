namespace RimGovernor.Brain.Policy;

/// <summary>
/// One producing comp, not harvested stock. Rates already incorporate the native interval, resource
/// nutrition and growth speed. Feed is the animal's own daily plant feed, which the channel nets out.
/// </summary>
public abstract record AnimalProduct
{
    private protected AnimalProduct() { }

    public abstract string Pawn { get; }
    public abstract string Race { get; }
    public abstract bool Reachable { get; }
    protected abstract RimGovernor.Brain.GameFacts.Observations.AnimalYield Yield { get; }
    private RimGovernor.Brain.GameFacts.Observations.AnimalYield.Producing Production => Yield as RimGovernor.Brain.GameFacts.Observations.AnimalYield.Producing
        ?? throw new RequiredFactException("producing animal yield");
    public double NutritionPerDay => Production.NutritionPerDay;
    public double WorkPerDay => Production.WorkPerDay;
    public double LeadDays => Production.LeadDays;
    public AnimalFeed Feed { get; init; } = new AnimalFeed.NoPlants();

    public sealed record Gatherable(RimGovernor.Brain.GameFacts.Observations.GatherableAnimal Observed) : AnimalProduct
    {
        public override string Pawn => Observed.PawnId;
        public override string Race => Observed.Race;
        public override bool Reachable => Observed.HandlerReachable;
        protected override RimGovernor.Brain.GameFacts.Observations.AnimalYield Yield => Observed.Yield;
    }

    public sealed record EggLayer(RimGovernor.Brain.GameFacts.Observations.EggLayerAnimal Observed) : AnimalProduct
    {
        public override string Pawn => Observed.PawnId;
        public override string Race => Observed.Race;
        public override bool Reachable => true;
        protected override RimGovernor.Brain.GameFacts.Observations.AnimalYield Yield => Observed.Yield;
    }
}
public static class FoodAnimalProductsRules
{
    private sealed class AnimalAggregate
    {
        public double Nutrition;
        public double Work;
        public double Lead = double.PositiveInfinity;
        public double Feed;
        public bool Unreachable;
        public HashSet<string> Pawns { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Aggregates independent comps by race into the rate net of the herd's
    /// feed (product minus feed per day, never below zero). An unreachable animal makes that race unavailable
    /// rather than silently claiming a partial herd yield.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> AnimalProductChannels(IReadOnlyList<AnimalProduct> animals)
    {
        var byRace = new Dictionary<string, AnimalAggregate>(StringComparer.Ordinal);
        foreach (var a in animals)
        {
            if (!byRace.TryGetValue(a.Race, out var r))
            {
                r = new AnimalAggregate();
                byRace[a.Race] = r;
            }
            // An animal that eats no plants nets out no feed.
            var f = a.Feed is AnimalFeed.Plants { PerDay: var perDay } ? perDay : 0;
            if (!FoodForecastRules.FoodId(a.Pawn) || !FoodForecastRules.FoodId(a.Race) || !FoodForecastRules.FoodNumber(a.NutritionPerDay) || !FoodForecastRules.FoodNumber(a.WorkPerDay) || !FoodForecastRules.FoodNumber(a.LeadDays) || !FoodForecastRules.FoodNumber(f))
            {
                r.Nutrition = double.NaN;
            }
            if (!a.Reachable)
            {
                r.Unreachable = true;
                continue;
            }
            r.Nutrition += a.NutritionPerDay;
            r.Work += a.WorkPerDay;
            if (r.Pawns.Add(a.Pawn))
            {
                r.Feed += f;
            }
            r.Lead = Math.Min(r.Lead, a.LeadDays);
        }
        var channels = new List<SupplyCandidate>();
        foreach (var race in byRace.Keys.Order(StringComparer.Ordinal))
        {
            var r = byRace[race];
            // A race with an unreachable animal is unpriced, unscheduled and not committed; a NaN rate still reaches the planner's check.
            YieldSupply supply = !r.Unreachable || double.IsNaN(r.Nutrition) ? new YieldSupply.Steady(Math.Max(0, r.Nutrition - r.Feed)) : new YieldSupply.Unpriced();
            var c = r.Unreachable
                ? CandidateRules.FoodCandidate(CandidateKind.AnimalProduct, race, supply, CandidateState.Closed, new CandidateLead.Unscheduled(), new CandidateWork.Unpriced())
                : CandidateRules.FoodCandidate(CandidateKind.AnimalProduct, race, supply, CandidateRules.FoodState(false, true), new CandidateLead.Days(r.Lead), new CandidateWork.PerDay(r.Work));
            c = c with
            {
                Source = "animal_product:" + race,
                Terms = [new CandidateTerm("productive_animals", r.Pawns.Count), new CandidateTerm("feed_per_day", r.Feed)],
            };
            channels.Add(c);
        }
        return channels;
    }

    /// <summary>
    /// Protects admitted productive animals when their nutrition per work exceeds the
    /// marginal crop channel. Operator floors are never lowered. An operator ceiling bounds a derived floor, but
    /// cannot erase an explicit floor.
    /// </summary>
    public static HerdPolicy FoodHerdPolicy(HerdPolicy herd, FoodPlan? plan)
    {
        if (plan is null)
        {
            return herd;
        }
        var minimum = new Dictionary<Resource, long>(herd.PopulationMin);
        var marginalCrop = double.PositiveInfinity;
        foreach (var e in plan.Portfolio)
        {
            var nk = e.Channel.NutritionRate(out var n);
            var wk = e.Channel.WorkRate(out var w);
            if (e.Channel.Kind == CandidateKind.Crop && nk && wk && n > 0 && w > 0)
            {
                marginalCrop = Math.Min(marginalCrop, n / w);
            }
        }
        if (double.IsPositiveInfinity(marginalCrop))
        {
            marginalCrop = 0;
        }
        foreach (var e in plan.Portfolio)
        {
            if (e.Channel.Kind != CandidateKind.AnimalProduct || e.Decision == FoodPlanDecision.Close || !e.Selected)
            {
                continue;
            }
            var nk = e.Channel.NutritionRate(out var n);
            var wk = e.Channel.WorkRate(out var w);
            if (!nk || !wk || n <= 0 || (w > 0 && n / w <= marginalCrop))
            {
                continue;
            }
            foreach (var term in e.Channel.Terms)
            {
                if (term.Name != "productive_animals" || !FoodForecastRules.FoodNumber(term.Value) || term.Value > 256)
                {
                    continue;
                }
                var floor = (long)Math.Ceiling(term.Value);
                var race = new Resource(e.Channel.Id);
                if (herd.PopulationMax.TryGetValue(race, out var ceiling))
                {
                    floor = Math.Min(floor, ceiling);
                }
                minimum[race] = Math.Max(minimum.GetValueOrDefault(race), floor);
            }
        }
        return herd with { PopulationMin = minimum };
    }
}
