namespace RimGovernor.Brain.Policy;

// Go: animal_feed_runway.go
// Animal feed runway. Each race group of the kept herd eats NutritionPerDay (the food forecast's per-animal
// rate); the pasture of the pens feeds part of it and the unheld stock every animal of the group can eat the
// rest. The runway is that stock over the net daily consumption, against MaintenanceHorizonDays. The shortfall is
// a forward-projector domain and the feed to stock ahead of it is an economy/resource level in the Rounder's
// construction memory, like FuelRunway. Unknown consumption, pasture, stock or feed leaves the projection
// unknown, never defaulted.
//
// Herd growth from known events is in it: a pregnant animal adds the catalog's mean litter, eating as a newborn
// from the due tick and stepping up each life stage after it, and a young animal steps up at its next life stage
// tick, both by the stages' hunger rate factors. A new conception stays reactive, and the litter is an estimate
// the census corrects once born. The pasture credit stays the present herd's: growth is fed from the stock.

/// <summary>What the animal feed runway is computed from.</summary>
public sealed record AnimalFeedInputs
{
    public Fact<EquatableArray<UpkeepAnimal>> Animals { get; init; }

    public EquatableArray<Resource> Directed { get; init; }

    public Fact<FoodSupply> Food { get; init; }

    public Fact<FoodForecast> Forecast { get; init; }

    /// <summary>The pens' worst-season pasture rates (PenGrazing).</summary>
    public Fact<EquatableArray<PenGrazing>> Pens { get; init; }

    public AnimalRaceCatalog Races { get; init; } = new();

    public StockReader Stock { get; init; }
}

/// <summary>A change of a group's consumption: from Tick (game ticks from now) the group eats Delta more nutrition per day.</summary>
public readonly record struct FeedStep(long Tick, double Delta);

/// <summary>The animal feed domain of the forward projection. ShortfallDays is the largest group shortfall.</summary>
public sealed record AnimalFeedProjection
{
    public double ShortfallDays { get; init; }

    /// <summary>A group the stock and pasture do not carry over the horizon.</summary>
    public bool IsShort() => ShortfallDays > 0;
}

/// <summary>The review's animal feed runway: Needs the stock levels the economy/resource Need must reach, the projection beside them.</summary>
public sealed record AnimalFeedRunway
{
    public IReadOnlyDictionary<Resource, long> Needs { get; init; } = new Dictionary<Resource, long>();

    public Fact<AnimalFeedProjection> Projection { get; init; }
}

public static class AnimalFeedRunwayExtensions
{
    /// <summary>PlanAnimalFeedRunway of the review's facts: the herd's food forecast is the food plan's when it is read.</summary>
    public static AnimalFeedRunway AnimalFeedRunway(this RoundsFacts f) => PlanAnimalFeedRunway(AnimalFeedInputsOf(f));

    internal static AnimalFeedInputs AnimalFeedInputsOf(RoundsFacts f)
    {
        var u = f.AnimalUpkeep;
        var input = new AnimalFeedInputs
        {
            Animals = u.Animals,
            Directed = u.DirectedHerds,
            Food = u.Food,
            Forecast = u.Forecast,
            Pens = f.PenGrazing,
            Races = u.AnimalRaces,
            Stock = new StockReader { Resources = f.Resources, Wood = f.Wood },
        };
        if (f.FoodPlan.TryGetValue(out var plan))
        {
            input = input with { Forecast = Known(plan.Forecast) };
        }

        return input;
    }
}

public static partial class PolicyFunctions
{
    private static AnimalFeedRunway UnknownFeedRunway() => new() { Projection = Unknown<AnimalFeedProjection>() };

    /// <summary>
    /// The consumption steps a pregnancy and the next life stages of one animal eating <paramref name="need"/> a
    /// day make. Not known when the race, a stage, the litter or the event ticks are not.
    /// </summary>
    internal static bool GrowthSteps(UpkeepAnimal a, double need, AnimalRaceCatalog races, out List<FeedStep> steps)
    {
        steps = [];
        var inCatalog = races.Race(a.Definition, out var race);
        var stages = race.LifeStages;
        var known = a.Herd.LifeStageIndex.TryGetValue(out var idx);
        if (!inCatalog || !known || idx < 0 || idx >= stages.Count)
        {
            return false;
        }

        var next = (int)idx + 1;
        if (next < stages.Count)
        {
            var tk = a.Herd.TicksToNextLifeStage.TryGetValue(out var ticks);
            var current = stages[(int)idx].HungerRateFactor;
            if (!tk || !(current > 0))
            {
                return false;
            }

            var prev = need;
            for (var k = next; k < stages.Count; k++)
            {
                var rate = need * stages[k].HungerRateFactor / current;
                steps.Add(new FeedStep(ticks + stages[k].MinAgeTicks - stages[next].MinAgeTicks, rate - prev));
                prev = rate;
            }
        }

        var pk = a.SlaughterFacts.Pregnant.TryGetValue(out var pregnant);
        if (!pk)
        {
            return false;
        }

        if (!pregnant)
        {
            return true;
        }

        var bk = a.Herd.TicksToBirth.TryGetValue(out var birth);
        var lk = race.LitterSize.TryGetValue(out var litter);
        var ak = race.AdultFeedPerDay.TryGetValue(out var adult);
        var last = stages[^1].HungerRateFactor;
        if (!bk || !lk || !ak || !(last > 0))
        {
            return false;
        }

        var before = 0.0;
        for (var k = 0; k < stages.Count; k++)
        {
            var s = stages[k];
            var rate = litter * adult * s.HungerRateFactor / last;
            var tick = (long)birth;
            if (k > 0)
            {
                tick += s.MinAgeTicks;
            }

            steps.Add(new FeedStep(tick, rate - before));
            before = rate;
        }

        return true;
    }

    /// <summary>
    /// How the stock lasts against a group eating <paramref name="baseRate"/> a day, less pasture, with steps
    /// changing the rate (all per day, steps in ticks): the nutrition missing over the horizon and the days the
    /// stock lasts, the horizon when it lasts.
    /// </summary>
    internal static (double Missing, double RunwayDays) RunwayOver(double baseRate, double pasture, double stock, IEnumerable<FeedStep> steps)
    {
        var ordered = PolicySort.ByLess(steps, (a, b) => a.Tick < b.Tick);
        double horizon = MaintenanceHorizonDays;
        double at = 0.0, rate = baseRate, eaten = 0.0;
        var runwayDays = horizon;
        var exhausted = false;

        void Advance(double to)
        {
            var net = Math.Max(0, rate - pasture);
            if (!exhausted && net > 0 && eaten + (net * (to - at)) > stock)
            {
                runwayDays = at + ((stock - eaten) / net);
                exhausted = true;
            }

            eaten += net * (to - at);
            at = to;
        }

        foreach (var s in ordered)
        {
            var day = (double)s.Tick / GameTime.TicksPerDay;
            if (day >= horizon)
            {
                break;
            }

            Advance(Math.Max(day, at));
            rate += s.Delta;
        }

        Advance(horizon);
        return (eaten - stock, runwayDays);
    }

    /// <summary>
    /// The animals the herd feed covers: not marked for release or slaughter and not of a directed herd. Unknown
    /// when an animal's removal flags are.
    /// </summary>
    internal static bool FeedEligible(IEnumerable<UpkeepAnimal> animals, IEnumerable<Resource> directed, out List<UpkeepAnimal> eligible)
    {
        var skip = new HashSet<Resource>(directed);
        eligible = [];
        foreach (var a in animals)
        {
            var rk = a.Release.TryGetValue(out var release);
            var sk = a.Slaughter.TryGetValue(out var slaughter);
            if (!rk || !sk)
            {
                eligible = [];
                return false;
            }

            if (!release && !slaughter && !skip.Contains(a.Definition))
            {
                eligible.Add(a);
            }
        }

        return true;
    }

    /// <summary>
    /// The item a short group is fed with: the lowest (defName, id) stock every animal of it can eat, else the
    /// cheapest item a recipe makes that its race can eat. Known is false for a race the catalog lacks; Ok false
    /// when nothing feeds it.
    /// </summary>
    internal static (Resource Item, double PerItem, bool Ok, bool Known) FeedItem(
        IReadOnlyList<PawnId> groupAnimals,
        Resource definition,
        IEnumerable<FoodStock> stocks,
        AnimalRaceCatalog races
    )
    {
        var item = default(Resource);
        var perItem = 0.0;
        var bestId = "";
        var found = false;
        foreach (var s in stocks)
        {
            if (!GroupEats(s, groupAnimals))
            {
                continue;
            }

            s.Count.TryGetValue(out var count);
            s.Nutrition.TryGetValue(out var nutrition);
            if (!found || s.DefName < item || (s.DefName == item && string.CompareOrdinal(s.Id, bestId) < 0))
            {
                item = s.DefName;
                bestId = s.Id;
                perItem = nutrition / count;
                found = true;
            }
        }

        if (found)
        {
            return (item, perItem, true, true);
        }

        if (!races.Race(definition, out var race))
        {
            return (default, 0, false, false);
        }

        foreach (var f in race.FeedItems)
        {
            if (!ValidResource(f.Def) || !FoodNumber(f.Nutrition) || f.Nutrition <= 0)
            {
                return (default, 0, false, false);
            }

            if (!found || f.Nutrition < perItem || (f.Nutrition == perItem && f.Def < item))
            {
                item = f.Def;
                perItem = f.Nutrition;
                found = true;
            }
        }

        return (item, perItem, found, true);
    }

    /// <summary>
    /// Rolls every race group of the herd forward over MaintenanceHorizonDays: the group eats its animals'
    /// NutritionPerDay less its share of the pens' pasture, the stock every animal of it can eat covers the
    /// rest for a runway of days, and a shorter runway demands the items of the feed (the stock's, else the
    /// cheapest producible) the horizon's missing nutrition takes, as an absolute stock level counted only where
    /// the stock falls short. A short group no item feeds is listed in Gaps.
    /// </summary>
    public static AnimalFeedRunway PlanAnimalFeedRunway(AnimalFeedInputs input)
    {
        if (!input.Animals.TryGetValue(out var animals))
        {
            return UnknownFeedRunway();
        }

        if (!FeedEligible(animals, input.Directed, out var eligible))
        {
            return UnknownFeedRunway();
        }

        if (eligible.Count == 0)
        {
            return new AnimalFeedRunway { Projection = Known(new AnimalFeedProjection()) };
        }

        var supplyKnown = input.Food.TryGetValue(out var supply);
        var pk = input.Pens.TryGetValue(out var pens);
        if (!supplyKnown || !pk)
        {
            return UnknownFeedRunway();
        }

        if (!input.Forecast.TryGetValue(out var forecast))
        {
            var ids = eligible.Select(a => a.Id).ToList();
            try
            {
                forecast = ForecastFood(supply!, ids);
            }
            catch (DomainException)
            {
                return UnknownFeedRunway();
            }
        }

        var perDay = new Dictionary<PawnId, double>();
        foreach (var row in forecast.Consumers)
        {
            perDay[row.Id] = row.NutritionPerDay;
        }

        var pasture = 0.0;
        foreach (var p in pens)
        {
            var dk = p.DemandPerDay.TryGetValue(out var d);
            var yk = p.PasturePerDay.TryGetValue(out var y);
            if (!dk || !yk || !FoodNumber(d) || !FoodNumber(y))
            {
                return UnknownFeedRunway();
            }

            pasture += Math.Min(d, y);
        }

        var byRace = new Dictionary<Resource, FeedGroupBuilder>();
        var total = 0.0;
        foreach (var a in eligible)
        {
            if (!perDay.TryGetValue(a.Id, out var need) || !FoodNumber(need))
            {
                return UnknownFeedRunway();
            }

            if (!byRace.TryGetValue(a.Definition, out var g))
            {
                g = new FeedGroupBuilder { Definition = a.Definition };
                byRace[a.Definition] = g;
            }

            if (!GrowthSteps(a, need, input.Races, out var steps))
            {
                return UnknownFeedRunway();
            }

            g.Animals.Add(a.Id);
            g.Steps.AddRange(steps);
            g.NutritionPerDay += need;
            total += need;
        }

        var needs = new Dictionary<Resource, long>();
        double projectionShortfall = 0;
        foreach (var g in byRace.Values)
        {
            foreach (var stock in supply!.Stocks)
            {
                if (GroupEats(stock, g.Animals))
                {
                    stock.Nutrition.TryGetValue(out var nutrition);
                    g.StockNutrition += nutrition;
                }
            }

            if (total > 0)
            {
                g.PasturePerDay = Math.Min(g.NutritionPerDay, pasture * g.NutritionPerDay / total);
            }

            if (!FoodNumber(g.StockNutrition) || !FoodNumber(g.NutritionPerDay))
            {
                return UnknownFeedRunway();
            }

            var (missing, runway) = RunwayOver(g.NutritionPerDay, g.PasturePerDay, g.StockNutrition, g.Steps);
            if (missing > 0)
            {
                g.ShortfallDays = MaintenanceHorizonDays - runway;
                var (item, perItem, ok, known) = FeedItem(g.Animals, g.Definition, supply.Stocks, input.Races);
                if (!known)
                {
                    return UnknownFeedRunway();
                }

                if (ok)
                {
                    if (!input.Stock.Count(item).TryGetValue(out var have))
                    {
                        return UnknownFeedRunway();
                    }

                    var need = have + (long)Math.Ceiling(missing / perItem);
                    needs[item] = Math.Max(needs.GetValueOrDefault(item), need);
                }
            }

            projectionShortfall = Math.Max(projectionShortfall, g.ShortfallDays);
        }

        return new AnimalFeedRunway
        {
            Needs = needs,
            Projection = Known(new AnimalFeedProjection { ShortfallDays = projectionShortfall }),
        };
    }

    /// <summary>Unheld stock with positive known count and nutrition that every one of the animals can eat.</summary>
    internal static bool GroupEats(FoodStock s, IReadOnlyList<PawnId> animals)
    {
        var hk = s.Holder.TryGetValue(out var holder);
        if (!hk || !holder.IsEmpty || s.DefName.IsEmpty || !ValidResource(s.DefName))
        {
            return false;
        }

        var ck = s.Count.TryGetValue(out var count);
        var nk = s.Nutrition.TryGetValue(out var nutrition);
        if (!ck || count <= 0 || !nk || !FoodNumber(nutrition) || nutrition <= 0)
        {
            return false;
        }

        var eaters = new HashSet<PawnId>(s.Eaters);
        foreach (var id in animals)
        {
            if (!eaters.Contains(id))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class FeedGroupBuilder
    {
        public Resource Definition { get; init; }

        public List<PawnId> Animals { get; } = [];

        public double NutritionPerDay { get; set; }

        public double PasturePerDay { get; set; }

        public double StockNutrition { get; set; }

        public List<FeedStep> Steps { get; } = [];

        public double ShortfallDays { get; set; }
    }
}
