using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

/// <summary>The trade inputs the food plan prices acquisition from.</summary>
internal sealed record FoodTrade
{
    public IReadOnlyList<TradeOffers> Offers { get; init; } = [];

    public long Silver { get; init; }

    public long Reserve { get; init; }

    public ulong Revision { get; init; }
}

public sealed partial class ColonyReview
{
    /// <summary>Plans food into the projection and marks the traders the offer book has not priced.</summary>
    internal void PlanFood(ref ColonyReading p)
    {
        p = p with { Facts = p.Facts with { FoodPlan = FoodPlanOf(p) } };
        MarkUnpriced(ref p);
    }

    /// <summary>The last hunt comparison for telemetry: its findings, the roster chosen, why the others were unusable and why a held formation was released.</summary>
    private static string DescribeHuntPlan(HuntPlan h, string released)
    {
        var chosen = h.Selection?.Selected.Roster?.Key ?? "none";
        var why = h.Selection is { } sel ? string.Join(",", sel.Alternatives.Where(a => a.Roster is not null).GroupBy(a => a.Refusal.Length == 0 ? "usable" : a.Refusal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key + "=" + g.Count())) : "";
        return $"chosen={chosen} rosters=[{why}] findings=[{string.Join(";", h.Findings)}]" + (released.Length == 0 ? "" : " released:" + released);
    }

    /// <summary>
    /// The cached food plan for the projection; recomputed when the census generation, the offers, the seasonal thresholds
    /// or the observed identity moved, or a held formation hunt is due for release, so the release's re-comparison is never skipped.
    /// </summary>
    internal FoodPlan FoodPlanOf(ColonyReading p)
    {
        var s = Census;
        lock (s.Mu)
        {
            var seasonal = Seasonal(p.Facts);
            var trade = FoodTradeOf(p);
            var huntRead = p.Facts.ForecastObserved.Coupled?.Hunt;
            if (s.FoodPlanHeld is { } held && s.FoodGeneration == s.Generation && s.FoodOffers == trade.Revision
                && s.FoodMin == seasonal.FoodMinDays && s.FoodTarget == seasonal.FoodTargetDays && s.FoodIdentity is { } foodIdentity && ReviewCensus.SameObservedIdentity(foodIdentity, p.Identity)
                && HuntAdmission.Release(huntRead, p.Facts.Acquisition, HuntHungry(p)).Length == 0)
            {
                return held;
            }

            var plan = ReviewFoodPlan(p, Policy, FoodCredit, HuntAdmission, trade, (h, released) => s.HuntNote = DescribeHuntPlan(h, released));
            HuntAdmission.Admit(HuntSquadRules.HuntRequest(plan).Select(id => (Domain.PawnId)id));
            LogFoodCredit(FoodCredit.Drain());
            if (FoodGapZero)
            {
                plan = plan with { GapPerDay = 0 };
            }

            s.FoodIdentity = p.Identity;
            s.FoodPlanHeld = plan;
            s.FoodGeneration = s.Generation;
            s.FoodMin = seasonal.FoodMinDays;
            s.FoodTarget = seasonal.FoodTargetDays;
            s.FoodOffers = trade.Revision;
            return plan;
        }
    }

    /// <summary>
    /// The food plan the last full review cached for <paramref name="world"/>'s colony, load and map; null when none
    /// is held for it. The immediate pass and the combat Need carry it forward, so a squad hunt the plan opened stays
    /// a combat deficit between full reviews (#3311).
    /// </summary>
    internal FoodPlan? HeldFoodPlan(GenerationSnapshot world)
    {
        var s = Census;
        lock (s.Mu)
        {
            var id = s.FoodIdentity;
            return id is not null && id.Colony == world.Colony && id.Load == world.Load && id.Map == world.Map ? s.FoodPlanHeld : null;
        }
    }

    internal FoodTrade FoodTradeOf(ColonyReading p)
    {
        var traders = p.Facts.Traders;
        var silver = p.Facts.Silver();
        var (reserve, rk) = TradePolicyRules.TradeSilverReserve(p.Facts.Census.ColonistCount);
        var output = new FoodTrade { Revision = TradeOffers.Version() };
        if (rk)
        {
            var world = new GenerationSnapshot { Colony = p.Identity.Colony, Map = p.Identity.Map, Load = p.Identity.Load };
            output = output with { Offers = TradeOffers.Fresh(world, p.Identity.Tick, silver, traders!), Silver = silver, Reserve = reserve };
        }

        return output;
    }

    /// <summary>Marks the traders whose offers the book has not priced, so the plan does not count them as a channel.</summary>
    internal void MarkUnpriced(ref ColonyReading p)
    {
        var traders = p.Facts.Traders;
        var silver = p.Facts.Silver();

        var world = new GenerationSnapshot { Colony = p.Identity.Colony, Map = p.Identity.Map, Load = p.Identity.Load };
        var priced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in TradeOffers.Fresh(world, p.Identity.Tick, silver, traders))
        {
            priced.Add(o.Trader);
        }

        var unpriced = traders.Where(t => t.CanTrade && !priced.Contains(t.Key())).Select(t => t.Key()).ToList();
        p = p with { Facts = p.Facts with { UnpricedTraders = EquatableArray.From(unpriced) } };
    }
}

partial class ColonyReview
{
    /// <summary>The colonists the projection reads too hungry to hunt (<see cref="HuntPlanningRules.Hungry"/>); none when the work census is unread.</summary>
    private static IReadOnlySet<string> HuntHungry(ColonyReading p) =>
        HuntPlanningRules.Hungry(p.Facts.ForecastObserved.WorkPawns.Pawns.Items);

    internal static FoodPlan ReviewFoodPlan(ColonyReading p, PlannerSettings thresholds, DeliveryCredit credit, HuntAdmission hunt, FoodTrade trade, Action<HuntPlan, string>? onHunt = null)
    {
        var supply = p.Facts.CombinedFoodSupply;

        var sources = p.Facts.Acquisition;

        var workers = ResearchRules.Workers(p.Facts.ForecastObserved.WorkPawns.Pawns);

        var forecast = FoodForecastRules.ForecastFood(supply, null);

        var human = p.Facts.FoodSupply;
        forecast = forecast.GateOnColonists(FoodConsumerIds(human), thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions).FoodMinDays);

        var gunners = 0;
        var hunters = 0;
        var pawns = p.Facts.ForecastObserved.WorkPawns.Pawns;
        {
            var profiles = PawnRules.Profiles(pawns);
            gunners = HuntSquadRules.ArmedHunters(profiles);
            hunters = PawnRules.Hunters(profiles);
        }

        var channels = new List<SupplyCandidate>();
        channels.AddRange(FoodPlanRules.ForageChannels(sources));
        // The hunt alternatives: nutrition the colony still needs to reach its target runway, compared over roster estimates.
        var huntRead = p.Facts.ForecastObserved.Coupled?.Hunt;
        var needed = FoodPlanRules.SeasonalShortfall(forecast, thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions).FoodTargetDays);
        var hungry = HuntHungry(p);
        var huntPlan = HuntPlanningRules.PlanHunt(huntRead, sources, p.Facts.ForecastObserved.Coupled?.Routes ?? [], needed, hungry);
        // The held formation stands until a release trigger; released, the plan's own choice is offered.
        var (formation, released) = hunt.Formation(huntPlan.Channel(sources), huntPlan, sources, huntRead, hungry);
        onHunt?.Invoke(huntPlan, released);
        channels.AddRange(HuntSquadRules.HuntCandidates(sources, gunners, formation));
        channels.AddRange(HuntPrerequisiteRules.HuntPrerequisiteCandidates(p.Facts.HuntHolds));
        channels = HuntCreditRules.HuntThroughput(channels, hunters, HuntCreditRules.HuntCadence(p.Snapshot.DeliveryLedger.Kills, hunters, p.Identity.Tick.Value)).ToList();
        var ids = human.Consumers.Select(c => c.Id).ToList();
        if (HumanFoodPlanRules.HumanFoodChannel(p.Facts.EssentialInputs.Benches, supply, ids, thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions).FoodTargetDays, p.Facts.Ideology) is { } channel)
        {
            channels.Add(channel);
        }

        if (p.Facts.FoodChannels.FishableWater is { } water)
        {
            var request = new FishingRequest { Research = water.Research, Regions = water.Regions };
            channels.AddRange(FishingRules.FishingChannels(request));
        }

        channels.AddRange(FoodAnimalProductsRules.AnimalProductChannels(p.Facts.FoodChannels.Products));

        channels.AddRange(MealRules.StockIngredientChannels(supply));
        var fields = p.Facts.FoodFields;
        {
            channels.AddRange(FoodPlanRules.CropChannels(
                fields,
                new CropKitchen { Benches = p.Facts.EssentialInputs.Benches, Cooks = workers },
                new CropSeason { Calendar = p.Facts.Calendar, Conditions = p.Facts.DisasterConditions }));
        }

        var (field, _) = FieldPlanner.FieldRequestOf(p, thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions).FoodTargetDays);
        channels.AddRange(FoodPlanRules.NewFieldChannels(field, new CropKitchen { Benches = p.Facts.EssentialInputs.Benches, Cooks = workers }));
        (CandidateKind Kind, string Id)[] supports = [(CandidateKind.Cook, "cooking-capacity"), (CandidateKind.Reserve, "stock-protection")];
        foreach (var support in supports)
        {
            channels.Add(FoodPlanRules.FoodSupportCandidate(support.Kind, support.Id));
        }

        channels = hunt.Apply(channels).ToList();
        channels = credit.Apply(channels, FoodCreditInput(p, credit)).ToList();
        var seasonal = thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions);
        var plan = FoodPlanRules.SupplyFoodPlan(new FoodPlanRequest
        {
            Demand = forecast,
            MinDays = seasonal.FoodMinDays,
            TargetDays = seasonal.FoodTargetDays,
            EmergencyDays = seasonal.FootholdFoodDays,
            Channels = EquatableArray.From(channels),
            Labor = FoodPlanRules.SupplyLabor(workers),
        });

        credit.Opened(plan, channels, p.Identity.Tick);
        if (plan.GapPerDay > 0)
        {
            var offers = new List<SupplyCandidate>();
            offers.AddRange(AnimalFoodRules.SlaughterFoodChannels(p.Facts.FoodChannels.Slaughter, p.Facts.AnimalUpkeep.Animals, HerdPlans.HerdPolicyOf(p.Facts, plan)));

            offers.AddRange(TradePolicyRules.TradeFoodChannels(trade.Offers, trade.Silver, trade.Reserve, FoodPlanRules.AcquisitionNutrition(plan)));
            var acquire = new AnimalAcquisition
            {
                Races = p.Facts.AnimalUpkeep.AnimalRaces,
                Owned = p.Facts.AnimalUpkeep.Animals,
                Wild = p.Facts.AnimalUpkeep.WildAnimals,
                Herd = HerdPlans.HerdPolicyOf(p.Facts, plan),
                Handlers = p.Facts.WorkProfiles,
            };
            offers.AddRange(AnimalAcquireRules.TameFoodChannels(acquire));
            offers.AddRange(AnimalAcquireRules.AnimalPurchaseFoodChannels(trade.Offers, acquire, trade.Silver, trade.Reserve));
            if (offers.Count > 0)
            {
                channels.AddRange(offers);
                plan = FoodPlanRules.SupplyFoodPlan(new FoodPlanRequest
                {
                    Demand = forecast,
                    MinDays = seasonal.FoodMinDays,
                    TargetDays = seasonal.FoodTargetDays,
                    EmergencyDays = seasonal.FootholdFoodDays,
                    Channels = EquatableArray.From(channels),
                    Labor = FoodPlanRules.SupplyLabor(workers),
                });
            }
        }

        var herd = HerdPlans.HerdPolicyOf(p.Facts, plan);
        var portfolio = plan.Portfolio.Select(e =>
        {
            if (e.Channel.Kind != CandidateKind.AnimalProduct)
            {
                return e;
            }

            herd.PopulationMin.TryGetValue(new Resource(e.Channel.Id), out var floor);
            return e with
            {
                Terms = [.. e.Terms, new CandidateTerm("effective_herd_floor", floor)],
                Reason = e.Reason + $"; animals/herd {e.Channel.Id} floor {floor}",
            };
        }).ToList();
        return plan with { Portfolio = portfolio };
    }

    internal static CreditInput FoodCreditInput(ColonyReading p, DeliveryCredit credit)
    {
        var ledger = p.Snapshot.DeliveryLedger;
        var delivered = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var count in ledger.Counts)
        {
            // Recipe output transforms existing food; it is not another acquisition channel.
            if (count.Kind == RimGovernor.Brain.GameFacts.Observations.DeliveryKind.Recipe) continue;
            var k = Wire.Name(count.Kind) + ":" + count.Source;
            delivered[k] = delivered.GetValueOrDefault(k) + count.Nutrition;
        }

        delivered[HuntCreditRules.HuntSource] = credit.HuntDelivered(ledger.Kills, ledger.Butchers, p.Facts.CombinedFoodSupply.Stocks, true);
        return new CreditInput { Tick = p.Identity.Tick, LoadToken = ledger.LoadToken, Delivered = delivered };
    }

    internal static void LogFoodCredit(IReadOnlyList<CreditChange> changes)
    {
        foreach (var c in changes)
        {
            Tel.Decide(FoodCreditDecision(c));
        }
    }

    internal static Decision FoodCreditDecision(CreditChange c) => new()
    {
        Kind = "food_credit",
        Component = "routine",
        Verdict = "credited",
        Reason = c.Reason,
        Target = c.Source,
        Attrs = new Dictionary<string, object?>
        {
            ["expected"] = c.Expected,
            ["observed"] = c.Observed,
            ["factor"] = c.Factor,
            ["window_days"] = c.WindowDays,
            ["state"] = c.State,
        },
    };

    internal static bool FoodPlanSupportOf(FoodPlan p, CandidateKind kind, string id) => FoodOrdersRules.FoodPlanSupport(p, kind, id);

    internal static bool FoodPlanOpensField(FoodPlan plan)
    {
        foreach (var entry in plan.Portfolio)
        {
            if (entry.Channel.Kind == CandidateKind.Crop && entry.Channel.Id.StartsWith(FoodPlanRules.NewFieldPrefix, StringComparison.Ordinal) && entry.Decision == FoodPlanDecision.Open)
            {
                return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<PawnId> FoodConsumerIds(FoodSupply supply) => supply.Consumers.Select(c => c.Id).ToList();
}
