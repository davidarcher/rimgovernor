namespace RimGovernor.Brain.Policy;

/// <summary>
/// One observed production comp, not harvested stock. Rates already incorporate the native interval, resource
/// nutrition and growth speed. FeedPerDay is the animal's own daily feed, which the channel nets out.
/// (Go <c>AnimalProduct</c>.)
/// </summary>
public sealed record AnimalProduct
{
    public string Pawn { get; init; } = "";

    public string Race { get; init; } = "";

    public Fact<bool> Active { get; init; }

    public Fact<bool> Reachable { get; init; }

    public Fact<double> NutritionPerDay { get; init; }

    public Fact<double> WorkPerDay { get; init; }

    public Fact<double> LeadDays { get; init; }

    public Fact<double> FeedPerDay { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>WithFeed</c>: sets each row's FeedPerDay from the upkeep census; an animal the census does not
    /// list, or one with an unread feed rate, stays unknown.
    /// </summary>
    public static IReadOnlyList<AnimalProduct> WithFeed(IReadOnlyList<AnimalProduct> rows, Fact<IReadOnlyList<UpkeepAnimal>> animals)
    {
        _ = animals.TryGetValue(out var census);
        var feed = new Dictionary<string, Fact<double>>(StringComparer.Ordinal);
        foreach (var a in census ?? [])
        {
            feed[a.Id.Value] = a.Herd.FeedPerDay;
        }
        return [.. rows.Select(row => feed.TryGetValue(row.Pawn, out var f) ? row with { FeedPerDay = f } : row)];
    }

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
    /// Go <c>AnimalProductChannels</c>: aggregates independent comps by race into the rate net of the herd's
    /// feed (product minus feed per day, never below zero). A missing rate or feed makes that race unknown
    /// rather than silently claiming a partial herd yield.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> AnimalProductChannels(IReadOnlyList<AnimalProduct> animals)
    {
        var byRace = new Dictionary<string, AnimalAggregate>(StringComparer.Ordinal);
        foreach (var a in animals)
        {
            var ak = a.Active.TryGetValue(out var active);
            if (ak && !active)
            {
                continue;
            }
            if (!byRace.TryGetValue(a.Race, out var r))
            {
                r = new AnimalAggregate();
                byRace[a.Race] = r;
            }
            var nk = a.NutritionPerDay.TryGetValue(out var n);
            var wk = a.WorkPerDay.TryGetValue(out var w);
            var lk = a.LeadDays.TryGetValue(out var l);
            var fk = a.FeedPerDay.TryGetValue(out var f);
            var rk = a.Reachable.TryGetValue(out var reachable);
            if (!FoodId(a.Pawn) || !FoodId(a.Race) || (nk && !FoodNumber(n)) || (wk && !FoodNumber(w)) || (lk && !FoodNumber(l)) || (fk && !FoodNumber(f)))
            {
                r.Nutrition = double.NaN;
            }
            if (!ak || !nk || !wk || !lk || !fk || !rk || !reachable)
            {
                r.Unknown = true;
                continue;
            }
            r.Nutrition += n;
            r.Work += w;
            r.Feed += f;
            r.Lead = Math.Min(r.Lead, l);
            r.Pawns.Add(a.Pawn);
        }
        var channels = new List<SupplyCandidate>();
        foreach (var race in byRace.Keys.Order(StringComparer.Ordinal))
        {
            var r = byRace[race];
            var c = FoodCandidate(CandidateKind.AnimalProduct, race, Unknown<double>());
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
                c = c with { LaborPerDay = Known(r.Work), LeadDays = Known(r.Lead), State = FoodState(Known(false), Known(true)) };
            }
            channels.Add(c);
        }
        return channels;
    }

    /// <summary>
    /// Go <c>FoodHerdPolicy</c>: protects admitted productive animals when their nutrition per work exceeds the
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
            if (e.Channel.Kind != CandidateKind.AnimalProduct || e.Decision == FoodPlanDecision.Close || !e.Selected())
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
                if (term.Name != "productive_animals" || !FoodNumber(term.Value) || term.Value > 256)
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
