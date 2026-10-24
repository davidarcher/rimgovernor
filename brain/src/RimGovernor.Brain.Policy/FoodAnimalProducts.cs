namespace RimGovernor.Brain.Policy;

/// <summary>
/// One producing comp, not harvested stock. Rates already incorporate the native interval, resource
/// nutrition and growth speed. Feed is the animal's own daily plant feed, which the channel nets out.
/// </summary>
public sealed record AnimalProduct
{
    public string Pawn { get; init; } = "";

    public string Race { get; init; } = "";

    public bool Reachable { get; init; }

    public double NutritionPerDay { get; init; }

    public double WorkPerDay { get; init; }

    public double LeadDays { get; init; }

    public AnimalFeed Feed { get; init; } = new AnimalFeed.NoPlants();
}

public static class FoodAnimalProductsRules
{
    private sealed class AnimalAggregate
    {
        public double Nutrition;
        public double Work;
        public double Lead = double.PositiveInfinity;
        public double Feed;
        public bool Unknown;
        public HashSet<string> Pawns { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Aggregates independent comps by race into the rate net of the herd's
    /// feed (product minus feed per day, never below zero). An unreachable animal makes that race unknown
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
                r.Unknown = true;
                continue;
            }
            r.Nutrition += a.NutritionPerDay;
            r.Work += a.WorkPerDay;
            r.Feed += f;
            r.Lead = Math.Min(r.Lead, a.LeadDays);
            r.Pawns.Add(a.Pawn);
        }
        var channels = new List<SupplyCandidate>();
        foreach (var race in byRace.Keys.Order(StringComparer.Ordinal))
        {
            var r = byRace[race];
            var c = CandidateRules.FoodCandidate(CandidateKind.AnimalProduct, race, Unknown<double>());
            c = c with
            {
                Source = "animal_product:" + race,
                Terms = [new CandidateTerm("productive_animals", r.Pawns.Count), new CandidateTerm("feed_per_day", r.Feed)],
            };
            if (!r.Unknown || double.IsNaN(r.Nutrition))
            {
                c = c with { Yields = [c.Yields[0] with { PerDay = Known(Math.Max(0, r.Nutrition - r.Feed)) }, .. c.Yields.Skip(1)] };
            }
            if (!r.Unknown)
            {
                c = c with { LaborPerDay = Known(r.Work), LeadDays = Known(r.Lead), State = CandidateRules.FoodState(Known(false), Known(true)) };
            }
            channels.Add(c);
        }
        return channels;
    }

    /// <summary>
    /// Protects admitted productive animals when their nutrition per work exceeds the
    /// marginal crop channel. Operator floors are never lowered. An operator ceiling bounds a derived floor, but
    /// cannot erase an explicit floor.
    /// </summary>
    public static HerdPolicy FoodHerdPolicy(HerdPolicy herd, Fact<FoodPlan> fact)
    {
        if (!fact.TryGetValue(out var plan))
        {
            return herd;
        }
        var minimum = new Dictionary<Resource, long>(herd.PopulationMin);
        var marginalCrop = double.PositiveInfinity;
        foreach (var e in plan.Portfolio)
        {
            var nk = e.Channel.Nutrition().PerDay.TryGetValue(out var n);
            var wk = e.Channel.LaborPerDay.TryGetValue(out var w);
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
            var nk = e.Channel.Nutrition().PerDay.TryGetValue(out var n);
            var wk = e.Channel.LaborPerDay.TryGetValue(out var w);
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
