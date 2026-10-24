namespace RimGovernor.Brain.Policy;

// Acquiring animals for food: a wild tameable animal and a trader's live animal are one-animal candidates beside
// slaughter. Both yield what the animal would give once it is the colony's, net of its feed: milk or eggs per day
// after the lead (the race's first milkable or reproductive stage), else its meat as a one-shot stock once
// adult. The husbandry planners and the trade planner stay the executors; they act on the entries the plan opens.

/// <summary>The census the acquisition candidates price from.</summary>
public sealed record AnimalAcquisition
{
    public required AnimalRaceCatalog Races { get; init; }

    public EquatableArray<UpkeepAnimal> Owned { get; init; }

    public EquatableArray<UpkeepAnimal> Wild { get; init; }

    public required HerdPolicy Herd { get; init; }

    public Fact<EquatableArray<PawnProfile>> Handlers { get; init; }

    /// <summary>Whether the herd can take another animal of the race: not retired and below its ceiling when it has one.</summary>
    internal bool Room(Resource race)
    {
        if (Herd.Retired.GetValueOrDefault(race))
        {
            return false;
        }

        if (!Herd.PopulationMax.TryGetValue(race, out var ceiling))
        {
            return true;
        }

        var count = 0L;
        foreach (var a in Owned)
        {
            if (a.Definition == race && !a.Release)
            {
                count++;
            }
        }

        return count < ceiling;
    }

    /// <summary>
    /// Prices one animal of race and gender, ageTicks old: the milk and eggs it gives net of feed, else its
    /// meat. Not ok when any fact it needs is unread.
    /// </summary>
    internal static bool Yield(AnimalRace race, bool female, double ageTicks, out AcquiredYield result)
    {
        result = default;
        if (race.AdultFeed is not RaceFeed.Eats { PerDay: var feed } || !FoodForecastRules.FoodNumber(feed))
        {
            return false;
        }

        var rate = 0.0;
        var productLead = 0.0;
        var products = false;
        foreach (var p in race.Products)
        {
            // A hen lays from her first breeding stage, else from adulthood; a race that never gives milk yields none.
            RaceStage stage;
            switch (p.Kind)
            {
                case "eggs":
                    stage = race.Reproductive is RaceStage.Reaches ? race.Reproductive : new RaceStage.Reaches(race.AdultMinAgeTicks);
                    break;
                case "milk":
                    stage = race.Milkable;
                    break;
                default:
                    continue;
            }

            if (stage is not RaceStage.Reaches { Ticks: var ticks })
            {
                continue;
            }

            var amount = p.Amount;
            var interval = p.IntervalDays;
            var days = Math.Max(0, (ticks - ageTicks) / GameTime.TicksPerDay);
            if (p.Food is not RaceProductFood.Feeds { PerUnit: var nutrition } || interval <= 0)
            {
                return false;
            }

            if (!female && (p.Kind == "milk" || p.FemaleOnly))
            {
                continue;
            }

            rate += amount / interval * nutrition;
            productLead = Math.Max(productLead, days);
            products = true;
        }

        if (products && rate > feed)
        {
            result = new AcquiredYield(new YieldSupply.Steady(rate - feed), productLead, feed);
            return true;
        }

        var leadDays = Math.Max(0, (race.AdultMinAgeTicks - ageTicks) / GameTime.TicksPerDay);
        if (race.Meat is not RaceMeat.Yields { Amount: var meatAmount, NutritionPerUnit: var per })
        {
            return false;
        }

        // The animal eats until it is slaughtered adult.
        var meat = Math.Floor((meatAmount * per) - (feed * leadDays));
        if (meat < 1)
        {
            return false;
        }

        result = new AcquiredYield(new YieldSupply.Stock((long)meat), leadDays, feed);
        return true;
    }
}

/// <summary>What one acquired animal yields: a net rate or a stock.</summary>
internal readonly record struct AcquiredYield(YieldSupply Supply, double LeadDays, double Feed)
{
    internal SupplyCandidate Channel(CandidateKind kind, string id, double upfront, IEnumerable<CandidateTerm> terms)
    {
        var c = CandidateRules.FoodCandidate(kind, id, Supply, CandidateState.Closed, new CandidateLead.Days(LeadDays), new CandidateWork.PerDay(0));
        return c with
        {
            UpfrontCost = c.UpfrontCost with { LaborTicks = upfront },
            Terms = EquatableArray.From(new[] { new CandidateTerm("feed_per_day", Feed) }.Concat(terms)),
        };
    }
}

public static class AnimalAcquireRules
{
    /// <summary>The talk toils of one taming interaction: game code structure (JobDriver_InteractAnimal), not a value the game exposes.</summary>
    internal const int AnimalInteractionTalks = 3;

    /// <summary>Ranks acquisitions of equal kind: yield (a rate, else a stock) per unit of upfront work.</summary>
    internal static double AcquisitionValue(SupplyCandidate c)
    {
        c.NutritionRate(out var n);
        long s = 0;
        if (c.Nutrition() is { } y)
        {
            y.StockOf(out s);
        }
        return (n + s) / Math.Max(1, c.UpfrontCost.LaborTicks);
    }

    /// <summary>
    /// Keeps the best rate candidate and the best one-shot stock candidate: the two are not comparable, the
    /// plan chooses between them.
    /// </summary>
    internal static List<SupplyCandidate> BestAcquisition(IEnumerable<SupplyCandidate> channels)
    {
        var sorted = PolicySort.ByLess(
            channels,
            (a, b) =>
            {
                double va = AcquisitionValue(a), vb = AcquisitionValue(b);
                return va != vb ? va > vb : string.CompareOrdinal(a.Id, b.Id) < 0;
            }
        );
        var output = new List<SupplyCandidate>();
        bool rate = false, stock = false;
        foreach (var c in sorted)
        {
            var isRate = c.NutritionRate(out _);
            if (isRate && !rate)
            {
                output.Add(c);
                rate = true;
            }
            else if (!isRate && !stock)
            {
                output.Add(c);
                stock = true;
            }
        }

        return output;
    }

    /// <summary>
    /// Offers the best wild animals to tame (one for a rate, one for a stock): one a handler skilled enough can
    /// tame, tameable and not designated, of a race that is not dangerous, with herd room and feed. Its upfront
    /// work is the expected taming attempts (1 over the race's tame chance factor, the tamer's own chance
    /// unread) of three talks and the feeds each, its lead the time until the animal reaches the stage its
    /// yield needs, and failing an attempt risks the race's manhunter chance.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> TameFoodChannels(AnimalAcquisition input)
    {
        var wild = input.Wild;
        if (!input.Handlers.TryGetValue(out var profiles) || input.Herd.FeedShort)
        {
            return [];
        }

        var talk = input.Races.Interaction.TalkTicks;
        var feedTicks = input.Races.Interaction.FeedTicks;
        var feeds = input.Races.Interaction.Feeds;

        var output = new List<SupplyCandidate>();
        foreach (var a in wild)
        {
            var rk = input.Races.Race(a.Definition, out var race);
            var factor = race.TameChanceFactor;
            var years = a.Herd.AgeYears;
            var minimum = a.MinimumHandlingSkill;
            if (
                !a.Tameable
                || a.Tame
                || !rk
                || !(factor > 0)
                || HerdSurplusRules.HerdDangerous(a)
                || !input.Room(a.Definition)
            )
            {
                continue;
            }

            if (PawnRules.TamerFor(profiles, minimum) is null)
            {
                continue;
            }

            if (!AnimalAcquisition.Yield(race, a.Gender == RimGovernor.Contracts.Wire.Defs.Gender.Female, years * GameTime.DaysPerYear * GameTime.TicksPerDay, out var y))
            {
                continue;
            }

            var attempts = 1 / Math.Min(1, factor);
            var manhunter = race.ManhunterOnTameFail;
            var c = y.Channel(
                CandidateKind.Tame,
                "tame:" + a.Id,
                attempts * ((AnimalInteractionTalks * (double)talk) + ((double)feeds * feedTicks)),
                [new CandidateTerm("expected_attempts", attempts), new CandidateTerm("talk_toils_per_attempt", AnimalInteractionTalks)]
            );
            var risk = Math.Min(1, manhunter * (attempts - 1));
            if (risk > 0)
            {
                c = c with { Risk = EquatableArray.From([new CandidateRisk(CandidateRiskKind.Revenge, risk)]) };
            }

            output.Add(c);
        }

        return BestAcquisition(output);
    }

    /// <summary>
    /// Offers, per trader the colony has recorded, the best live animal it sells: affordable above the reserve,
    /// of a race with herd room, a known sex and yield. The silver is the upfront work (as trade food), the lead
    /// zero: a trader's animal is read as adult, its age is not on the sheet. The ID names the trader, race and
    /// sex the trade planner buys.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> AnimalPurchaseFoodChannels(IEnumerable<TradeOffers> offers, AnimalAcquisition input, long silver, long reserve)
    {
        var output = new List<SupplyCandidate>();
        foreach (var record in offers)
        {
            var perTrader = new List<SupplyCandidate>();
            foreach (var row in record.Rows)
            {
                var ok = input.Races.Race(new Resource(row.Def), out var race);
                if (
                    row is not TradeOffer.Pawn { Gender: var gender }
                    || !ok
                    || row.Count < 1
                    || gender == RimGovernor.Contracts.Wire.Defs.Gender.None
                    || row.Def.Contains('/', StringComparison.Ordinal)
                    || !TradePolicyRules.Finite(row.Price)
                    || row.Price <= 0
                    || row.Price > silver - reserve
                    || !input.Room(race.Def)
                )
                {
                    continue;
                }

                if (!AnimalAcquisition.Yield(race, gender == RimGovernor.Contracts.Wire.Defs.Gender.Female, double.PositiveInfinity, out var y))
                {
                    continue;
                }

                perTrader.Add(
                    y.Channel(
                        CandidateKind.AnimalBuy,
                        record.Trader + "/" + row.Def + "/" + gender,
                        row.Price * AcquisitionCatalogRules.TradeLaborPerSilver,
                        [new CandidateTerm("trade_silver", row.Price)]
                    )
                );
            }

            output.AddRange(BestAcquisition(perTrader));
        }

        return output;
    }

    /// <summary>The wild animal the plan opened to tame, while a handler skilled enough exists and the herd's feed is known not short.</summary>
    public static HusbandryChoice FoodTameChoice(
        FoodPlan plan,
        EquatableArray<UpkeepAnimal> wild,
        Fact<bool> feedShort,
        Fact<EquatableArray<PawnProfile>> handlers
    )
    {
        var fk = feedShort.TryGetValue(out var short_);
        var hk = handlers.TryGetValue(out var profiles);
        if (!fk || !hk)
        {
            return new HusbandryChoice.Unread();
        }

        if (short_)
        {
            return new HusbandryChoice.NoDeficit();
        }

        foreach (var e in plan.Portfolio)
        {
            if (e.Channel.Kind != CandidateKind.Tame || e.Decision != FoodPlanDecision.Open)
            {
                continue;
            }

            foreach (var a in wild)
            {
                var minimum = a.MinimumHandlingSkill;
                if (PawnRules.TamerFor(profiles, minimum) is not null && e.Channel.Id == "tame:" + a.Id)
                {
                    if (!a.Tame)
                    {
                        return new HusbandryChoice.Write(a.Id, HusbandryMethod.Tame);
                    }
                }
            }
        }

        return new HusbandryChoice.NoDeficit();
    }

    /// <summary>The animals the plan opened to buy from trader, as the wants SelectAnimalPurchase fills.</summary>
    public static IReadOnlyList<HerdWant> PlannedAnimalPurchases(FoodPlan plan, string trader)
    {
        var output = new List<HerdWant>();
        foreach (var e in plan.Portfolio)
        {
            var prefix = trader + "/";
            var ok = e.Channel.Id.StartsWith(prefix, StringComparison.Ordinal);
            var rest = ok ? e.Channel.Id[prefix.Length..] : e.Channel.Id;
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            var split = slash >= 0;
            var race = split ? rest[..slash] : rest;
            var gender = split ? rest[(slash + 1)..] : "";
            if (e.Channel.Kind == CandidateKind.AnimalBuy && e.Decision == FoodPlanDecision.Open && ok && split)
            {
                output.Add(new HerdWant { Race = new Resource(race), Male = gender == "Male", Female = gender == "Female" });
            }
        }

        return output;
    }
}
