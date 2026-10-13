namespace RimGovernor.Brain.Policy;

// Go: animal_acquire.go
// Acquiring animals for food: a wild tameable animal and a trader's live animal are one-animal candidates beside
// slaughter. Both yield what the animal would give once it is the colony's, net of its feed: milk or eggs per day
// after the lead (the race's first milkable or reproductive stage), else its meat as a one-shot stock once
// adult. The husbandry planners and the trade planner stay the executors; they act on the entries the plan opens.

/// <summary>The census the acquisition candidates price from.</summary>
public sealed record AnimalAcquisition
{
    public required AnimalRaceCatalog Races { get; init; }

    public Fact<EquatableArray<UpkeepAnimal>> Owned { get; init; }

    public Fact<EquatableArray<UpkeepAnimal>> Wild { get; init; }

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

        Owned.TryGetValue(out var owned);
        var count = 0L;
        foreach (var a in owned)
        {
            if (a.Release.TryGetValue(out var release) && a.Definition == race && !release)
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
    internal bool Yield(AnimalRace race, string gender, double ageTicks, out AcquiredYield result)
    {
        result = default;
        if (!race.AdultFeedPerDay.TryGetValue(out var feed) || !FoodNumber(feed))
        {
            return false;
        }

        (double, bool) Lead(Fact<long> stage)
        {
            var known = stage.TryGetValue(out var ticks);
            return (Math.Max(0, (ticks - ageTicks) / GameTime.TicksPerDay), known);
        }

        var rate = 0.0;
        var productLead = 0.0;
        var products = false;
        foreach (var p in race.Products)
        {
            var stage = race.MilkableMinAgeTicks;
            switch (p.Kind)
            {
                case "eggs":
                    stage = race.ReproductiveMinAgeTicks;
                    if (!stage.IsKnown)
                    {
                        stage = race.AdultMinAgeTicks;
                    }

                    break;
                case "milk":
                    break;
                default:
                    continue;
            }

            var ak = p.Amount.TryGetValue(out var amount);
            var ik = p.IntervalDays.TryGetValue(out var interval);
            var nk = p.NutritionPerUnit.TryGetValue(out var nutrition);
            var (days, sk) = Lead(stage);
            if (!sk)
            {
                continue;
            }

            if (!ak || !ik || !nk || interval <= 0)
            {
                return false;
            }

            if (gender != "Female" && (p.Kind == "milk" || p.FemaleOnly))
            {
                continue;
            }

            rate += amount / interval * nutrition;
            productLead = Math.Max(productLead, days);
            products = true;
        }

        if (products && rate > feed)
        {
            result = new AcquiredYield(Known(rate - feed), Unknown<long>(), productLead, feed);
            return true;
        }

        var mk = race.MeatAmount.TryGetValue(out var meatAmount);
        var pk = race.MeatNutritionPerUnit.TryGetValue(out var per);
        var (leadDays, ak2) = Lead(race.AdultMinAgeTicks);
        if (!mk || !pk || !ak2)
        {
            return false;
        }

        // The animal eats until it is slaughtered adult.
        var meat = Math.Floor((meatAmount * per) - (feed * leadDays));
        if (meat < 1)
        {
            return false;
        }

        result = new AcquiredYield(Unknown<double>(), Known((long)meat), leadDays, feed);
        return true;
    }
}

/// <summary>What one acquired animal yields: a net rate or a stock.</summary>
internal readonly record struct AcquiredYield(Fact<double> NutritionPerDay, Fact<long> Stock, double LeadDays, double Feed)
{
    internal SupplyCandidate Channel(CandidateKind kind, string id, double upfront, IEnumerable<CandidateTerm> terms)
    {
        var c = FoodCandidate(kind, id, NutritionPerDay);
        var yields = c.Yields.Items.ToBuilder();
        yields[0] = yields[0] with { StockCap = Stock };
        return c with
        {
            Yields = yields.ToImmutable(),
            LaborPerDay = Known(0.0),
            UpfrontCost = c.UpfrontCost with { LaborTicks = Known(upfront) },
            LeadDays = Known(LeadDays),
            State = FoodState(Known(false), Unknown<bool>()),
            Terms = EquatableArray.From(new[] { new CandidateTerm("feed_per_day", Feed) }.Concat(terms)),
        };
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The talk toils of one taming interaction: game code structure (JobDriver_InteractAnimal), not a value the game exposes.</summary>
    internal const int AnimalInteractionTalks = 3;

    /// <summary>Ranks acquisitions of equal kind: yield (a rate, else a stock) per unit of upfront work.</summary>
    internal static double AcquisitionValue(SupplyCandidate c)
    {
        c.Nutrition().PerDay.TryGetValue(out var n);
        c.Nutrition().StockCap.TryGetValue(out var s);
        c.UpfrontCost.LaborTicks.TryGetValue(out var u);
        return (n + s) / Math.Max(1, u);
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
                return va != vb ? va > vb : string.CompareOrdinal(a.ID, b.ID) < 0;
            }
        );
        var output = new List<SupplyCandidate>();
        bool rate = false, stock = false;
        foreach (var c in sorted)
        {
            var isRate = c.Nutrition().PerDay.IsKnown;
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
        var wk = input.Wild.TryGetValue(out var wild);
        var pk = input.Handlers.TryGetValue(out var profiles);
        if (!wk || !pk || input.Herd.FeedShort)
        {
            return [];
        }

        var tk = input.Races.Interaction.TalkTicks.TryGetValue(out var talk);
        var fk = input.Races.Interaction.FeedTicks.TryGetValue(out var feedTicks);
        var sk = input.Races.Interaction.Feeds.TryGetValue(out var feeds);
        if (!tk || !fk || !sk)
        {
            return [];
        }

        var output = new List<SupplyCandidate>();
        foreach (var a in wild)
        {
            var tameableKnown = a.Tameable.TryGetValue(out var tameable);
            var designatedKnown = a.Tame.TryGetValue(out var designated);
            var rk = input.Races.Race(a.Definition, out var race);
            var ck = race.TameChanceFactor.TryGetValue(out var factor);
            var yk = a.Herd.AgeYears.TryGetValue(out var years);
            a.MinimumHandlingSkill.TryGetValue(out var minimum);
            if (
                !tameableKnown
                || !tameable
                || !designatedKnown
                || designated
                || !rk
                || !ck
                || !(factor > 0)
                || !yk
                || HerdDangerous(a)
                || !input.Room(a.Definition)
            )
            {
                continue;
            }

            if (!TamerFor(profiles, minimum, out _))
            {
                continue;
            }

            if (!input.Yield(race, a.Gender, years * GameTime.DaysPerYear * GameTime.TicksPerDay, out var y))
            {
                continue;
            }

            var attempts = 1 / Math.Min(1, factor);
            race.ManhunterOnTameFail.TryGetValue(out var manhunter);
            var c = y.Channel(
                CandidateKind.Tame,
                "tame:" + a.ID,
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
                    !row.Pawn
                    || !ok
                    || row.Count < 1
                    || row.Gender.Length == 0
                    || row.Def.Contains('/', StringComparison.Ordinal)
                    || !Finite(row.Price)
                    || row.Price <= 0
                    || row.Price > silver - reserve
                    || !input.Room(race.Def)
                )
                {
                    continue;
                }

                if (!input.Yield(race, row.Gender, double.PositiveInfinity, out var y))
                {
                    continue;
                }

                perTrader.Add(
                    y.Channel(
                        CandidateKind.AnimalBuy,
                        record.Trader + "/" + row.Def + "/" + row.Gender,
                        row.Price * TradeLaborPerSilver,
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
        Fact<FoodPlan> plan,
        Fact<EquatableArray<UpkeepAnimal>> wild,
        Fact<bool> feedShort,
        Fact<EquatableArray<PawnProfile>> handlers
    )
    {
        var pk = plan.TryGetValue(out var p);
        var rk = wild.TryGetValue(out var rows);
        var fk = feedShort.TryGetValue(out var short_);
        var hk = handlers.TryGetValue(out var profiles);
        if (!pk || !rk || !fk || !hk)
        {
            return new HusbandryChoice { Reason = HusbandryReason.Unknown };
        }

        if (short_)
        {
            return new HusbandryChoice { Reason = HusbandryReason.NoDeficit };
        }

        foreach (var e in p.Portfolio)
        {
            if (e.Channel.Kind != CandidateKind.Tame || e.Decision != FoodPlanDecision.Open)
            {
                continue;
            }

            foreach (var a in rows)
            {
                a.MinimumHandlingSkill.TryGetValue(out var minimum);
                if (TamerFor(profiles, minimum, out _) && e.Channel.ID == "tame:" + a.ID)
                {
                    if (a.Tame.TryGetValue(out var designated) && !designated)
                    {
                        return new HusbandryChoice { Animal = a.ID, Method = HusbandryMethod.Tame };
                    }
                }
            }
        }

        return new HusbandryChoice { Reason = HusbandryReason.NoDeficit };
    }

    /// <summary>The animals the plan opened to buy from trader, as the wants SelectAnimalPurchase fills.</summary>
    public static IReadOnlyList<HerdWant> PlannedAnimalPurchases(Fact<FoodPlan> plan, string trader)
    {
        if (!plan.TryGetValue(out var p))
        {
            return [];
        }

        var output = new List<HerdWant>();
        foreach (var e in p.Portfolio)
        {
            var prefix = trader + "/";
            var ok = e.Channel.ID.StartsWith(prefix, StringComparison.Ordinal);
            var rest = ok ? e.Channel.ID[prefix.Length..] : e.Channel.ID;
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
