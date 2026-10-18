using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The trade inputs the food plan prices acquisition from.</summary>
internal sealed record FoodTrade
{
    public IReadOnlyList<TradeOffers> Offers { get; init; } = [];

    public long Silver { get; init; }

    public long Reserve { get; init; }

    public ulong Revision { get; init; }
}

internal sealed record HuntInput
{
    public IReadOnlyList<HuntKill> Kills { get; init; } = [];

    public IReadOnlyList<HuntButcher> Butchers { get; init; } = [];
}

public sealed partial class Rounder
{
    /// <summary>Plans food into the projection and marks the traders the offer book has not priced.</summary>
    internal void PlanFood(ref ColonyProjection p)
    {
        p = p with { Facts = p.Facts with { FoodPlan = FoodPlanOf(p) } };
        MarkUnpriced(ref p);
    }

    /// <summary>The last hunt comparison for telemetry: its findings, the roster chosen, why the others were unusable and why a held formation was released.</summary>
    private static string DescribeHuntPlan(HuntPlan h, string released)
    {
        var chosen = h.Selection?.Selected.Roster?.Key ?? "none";
        var why = h.Selection is { } sel ? string.Join(",", sel.Alternatives.Where(a => a.Roster is not null).GroupBy(a => a.Refusal.Length == 0 ? "usable" : a.Refusal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key + "=" + g.Count())) : "";
        return $"chosen={chosen} rosters=[{why}] unestimated={h.Unestimated.Count} findings=[{string.Join(";", h.Findings)}]" + (released.Length == 0 ? "" : " released:" + released);
    }

    /// <summary>
    /// The cached food plan for the projection; recomputed when the census generation, the offers, the seasonal thresholds
    /// or the observed identity moved, or a held formation hunt is due for release, so the release's re-comparison is never skipped.
    /// </summary>
    internal Fact<FoodPlan> FoodPlanOf(ColonyProjection p)
    {
        var s = Census;
        lock (s.Mu)
        {
            var seasonal = Seasonal(p.Facts);
            var trade = FoodTradeOf(p);
            var huntRead = p.Facts.ForecastObserved.Coupled?.Hunt;
            var huntView = huntRead is not null && HuntEstimates is { } known ? known.Current(huntRead, p.Identity.Tick.Value) : HuntEstimateView.None;
            if (s.FoodPlanFact.IsKnown && s.FoodGeneration == s.Generation && s.FoodOffers == trade.Revision
                && s.FoodHunt == huntView.Revision && s.FoodMin == seasonal.FoodMinDays && s.FoodTarget == seasonal.FoodTargetDays && RoundsCensus.SameObservedIdentity(s.FoodIdentity, p.Identity)
                && (!p.Acquisition.TryGetValue(out var sources) || HuntAdmission.Release(huntRead, sources).Length == 0))
            {
                return s.FoodPlanFact;
            }

            var plan = ReviewFoodPlan(p, Policy, FoodCredit, HuntAdmission, trade, HuntEstimates, huntView, (h, released) => s.HuntNote = DescribeHuntPlan(h, released));
            if (plan.TryGetValue(out var v))
            {
                HuntAdmission.Admit(HuntSquadRules.HuntRequest(Known(v)).Select(id => (Domain.PawnId)id));
            }

            LogFoodCredit(FoodCredit.Drain());
            if (plan.TryGetValue(out v) && FoodGapZero)
            {
                plan = Known(v with { GapPerDay = 0 });
            }

            if (plan.IsKnown)
            {
                s.FoodIdentity = p.Identity;
                s.FoodPlanFact = plan;
                s.FoodGeneration = s.Generation;
                s.FoodMin = seasonal.FoodMinDays;
                s.FoodTarget = seasonal.FoodTargetDays;
                s.FoodOffers = trade.Revision;
                s.FoodHunt = huntView.Revision;
            }

            return plan;
        }
    }

    /// <summary>
    /// The food plan the last full review cached for <paramref name="world"/>'s colony, load and map; unknown when none
    /// is held for it. The immediate pass and the combat Need carry it forward, so a squad hunt the plan opened stays
    /// a combat deficit between full reviews (#3311).
    /// </summary>
    internal Fact<FoodPlan> HeldFoodPlan(GenerationSnapshot world)
    {
        var s = Census;
        lock (s.Mu)
        {
            var id = s.FoodIdentity;
            return s.FoodPlanFact.IsKnown && id.Colony == world.Colony && id.Load == world.Load && id.Map == world.Map ? s.FoodPlanFact : Unknown<FoodPlan>();
        }
    }

    internal FoodTrade FoodTradeOf(ColonyProjection p)
    {
        var tk = p.Facts.Traders.TryGetValue(out var traders);
        var sk = p.Facts.Silver().TryGetValue(out var silver);
        var (reserve, rk) = TradePolicyRules.TradeSilverReserve(p.Facts.Colonists);
        var output = new FoodTrade { Revision = TradeOffers.Version() };
        if (tk && sk && rk)
        {
            var world = new GenerationSnapshot { Colony = p.Identity.Colony, Map = p.Identity.Map, Load = p.Identity.Load };
            output = output with { Offers = TradeOffers.Fresh(world, p.Identity.Tick, silver, traders!), Silver = silver, Reserve = reserve };
        }

        return output;
    }

    /// <summary>Marks the traders whose offers the book has not priced, so the plan does not count them as a channel.</summary>
    internal void MarkUnpriced(ref ColonyProjection p)
    {
        if (!p.Facts.Traders.TryGetValue(out var traders) || !p.Facts.Silver().TryGetValue(out var silver))
        {
            return;
        }

        var world = new GenerationSnapshot { Colony = p.Identity.Colony, Map = p.Identity.Map, Load = p.Identity.Load };
        var priced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in TradeOffers.Fresh(world, p.Identity.Tick, silver, traders))
        {
            priced.Add(o.Trader);
        }

        var marked = traders.Select(t => t with { Unpriced = t.CanTrade && !priced.Contains(t.ID) }).ToList();
        p = p with { Facts = p.Facts with { Traders = Known(EquatableArray.From(marked)) } };
    }
}

partial class Rounder
{
    internal static Fact<FoodPlan> ReviewFoodPlan(ColonyProjection p, RoundsPolicy thresholds, DeliveryCredit credit, HuntAdmission hunt, FoodTrade trade, IHuntEstimates? estimates, HuntEstimateView huntView, Action<HuntPlan, string>? onHunt = null)
    {
        if (!p.CombinedFoodSupply.TryGetValue(out var supply) || !p.Acquisition.TryGetValue(out var sources))
        {
            return Unknown<FoodPlan>();
        }

        var wk = p.Workers.TryGetValue(out var workers);
        if (p.WorkPawns.TryGetValue(out var workPawns))
        {
            wk = ResearchRules.RoundsWorkers(workPawns).TryGetValue(out workers);
        }

        if (!wk)
        {
            return Unknown<FoodPlan>();
        }

        FoodForecast forecast;
        try
        {
            forecast = FoodForecastRules.ForecastFood(supply, null);
        }
        catch (DomainException)
        {
            return Unknown<FoodPlan>();
        }

        if (p.FoodSupply.TryGetValue(out var human))
        {
            forecast = forecast.GateOnColonists(FoodConsumerIds(human), thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions).FoodMinDays);
        }

        var gunners = 0;
        var hunters = 0;
        if (p.WorkPawns.TryGetValue(out var pawns))
        {
            var profiles = PawnRules.Profiles(pawns);
            gunners = HuntSquadRules.ArmedHunters(profiles);
            hunters = PawnRules.Hunters(profiles);
        }

        var channels = new List<SupplyCandidate>();
        channels.AddRange(FoodPlanRules.ForageChannels(sources));
        // The hunt alternatives: nutrition the colony still needs to reach its target runway, compared over roster estimates.
        var huntRead = p.Facts.ForecastObserved.Coupled?.Hunt;
        var needed = Math.Max(0, (forecast.Consumers.Sum(c => c.NutritionPerDay) * thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions).FoodTargetDays) - forecast.UsableNutrition);
        var huntPlan = HuntPlanningRules.PlanHunt(huntRead, sources, p.Facts.ForecastObserved.Coupled?.Routes ?? [], huntView, needed);
        // The held formation stands until a release trigger; released, the plan's own choice is offered and the service re-asks what went stale.
        var (formation, released) = hunt.Formation(huntPlan.Channel(sources), huntPlan, sources, huntRead);
        onHunt?.Invoke(huntPlan, released);
        if (huntRead is not null && huntPlan.Unestimated.Count > 0)
        {
            _ = estimates?.Request(huntRead, huntPlan.Unestimated, p.Identity.Tick.Value);
        }

        channels.AddRange(HuntSquadRules.HuntCandidates(sources, gunners, formation, huntPlan.Awaiting));
        channels.AddRange(HuntPrerequisiteRules.HuntPrerequisiteCandidates(p.HuntHolds));
        var hunting = FoodHuntInput(p);
        channels = HuntCreditRules.HuntThroughput(channels, hunters, HuntCreditRules.HuntCadence(hunting.Kills, hunters, p.Identity.Tick.Value)).ToList();
        if (p.ProductionBenches.TryGetValue(out var benches) && p.FoodSupply.TryGetValue(out human))
        {
            var ids = human.Consumers.Select(c => c.Id).ToList();
            var (channel, channelOk) = HumanFoodPlanRules.HumanFoodChannel(benches, supply, ids, thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions).FoodTargetDays, p.Facts.IdeologyRead());
            if (channelOk)
            {
                channels.Add(channel);
            }
        }

        if (p.FoodChannels.TryGetValue(out var census) && census.FishableWater.TryGetValue(out var water))
        {
            var regions = water.Regions.Select(region => new FishingRegion
            {
                Id = FishingRules.FishingRegionId(region.Root),
                Population = region.Population,
                MaxPopulation = region.MaxPopulation,
                NutritionPerFish = region.NutritionPerFish,
                FishPerBatch = region.FishPerBatch,
                WorkTicksPerBatch = region.WorkTicksPerBatch,
                PawnFishWorkCapacity = region.PawnFishWorkCapacity,
                Reachable = region.Reachable,
                Frozen = region.Frozen,
                Designated = region.Delivering,
                Source = $"fish:{region.Root.X},{region.Root.Z}",
                DistanceSquared = region.DistanceSquared,
            }).ToList();
            var request = new FishingRequest { Researched = water.FishingResearched, ResearchLeadDays = water.ResearchLeadDays, Regions = regions };
            try
            {
                channels.AddRange(FishingRules.FishingChannels(request));
            }
            catch (DomainException)
            {
                return Unknown<FoodPlan>();
            }
        }

        if (p.FoodChannels.TryGetValue(out var animals))
        {
            channels.AddRange(FoodAnimalProductsRules.AnimalProductChannels(FoodAnimalProductsRules.WithFeed(animals.AnimalProducts(), p.Facts.AnimalUpkeep.Animals)));
        }

        channels.AddRange(MealRules.StockIngredientChannels(supply));
        if (p.FoodFields.TryGetValue(out var fields))
        {
            channels.AddRange(FoodPlanRules.CropChannels(
                fields,
                new CropKitchen { Benches = p.ProductionBenches, Cooks = Known((double)workers) },
                new CropSeason { Calendar = p.Facts.Calendar, Conditions = p.Facts.DisasterConditions }));
        }

        var (field, _) = RoundsFieldPlanner.FieldRequestOf(p, thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions).FoodTargetDays);
        channels.AddRange(FoodPlanRules.NewFieldChannels(field, new CropKitchen { Benches = p.ProductionBenches, Cooks = Known((double)workers) }));
        (CandidateKind Kind, string Id)[] supports = [(CandidateKind.Cook, "cooking-capacity"), (CandidateKind.Reserve, "stock-protection")];
        foreach (var support in supports)
        {
            channels.Add(FoodPlanRules.FoodSupportCandidate(support.Kind, support.Id));
        }

        channels = hunt.Apply(channels).ToList();
        channels = credit.Apply(channels, FoodCreditInput(p, credit, hunting)).ToList();
        var seasonal = thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions);
        FoodPlan plan;
        try
        {
            plan = FoodPlanRules.SupplyFoodPlan(new FoodPlanRequest
            {
                Demand = forecast,
                MinDays = seasonal.FoodMinDays,
                TargetDays = seasonal.FoodTargetDays,
                EmergencyDays = seasonal.FootholdFoodDays,
                Channels = Known(EquatableArray.From(channels)),
                Labor = Known(workers * 20000.0),
            });
        }
        catch (DomainException)
        {
            return Unknown<FoodPlan>();
        }

        credit.Opened(plan, channels, p.Identity.Tick);
        if (plan.GapPerDay > 0)
        {
            var offers = new List<SupplyCandidate>();
            if (p.FoodChannels.TryGetValue(out animals))
            {
                offers.AddRange(AnimalFoodRules.SlaughterFoodChannels(animals.Slaughter, p.Facts.AnimalUpkeep.Animals, HerdPlans.HerdPolicyOf(p.Facts, plan)));
            }

            offers.AddRange(TradePolicyRules.TradeFoodChannels(trade.Offers, trade.Silver, trade.Reserve, plan.GapPerDay * plan.HorizonDays));
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
                try
                {
                    plan = FoodPlanRules.SupplyFoodPlan(new FoodPlanRequest
                    {
                        Demand = forecast,
                        MinDays = seasonal.FoodMinDays,
                        TargetDays = seasonal.FoodTargetDays,
                        EmergencyDays = seasonal.FootholdFoodDays,
                        Channels = Known(EquatableArray.From(channels)),
                        Labor = Known(workers * 20000.0),
                    });
                }
                catch (DomainException)
                {
                    return Unknown<FoodPlan>();
                }
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
        return Known(plan with { Portfolio = portfolio });
    }

    internal static HuntInput FoodHuntInput(ColonyProjection p)
    {
        if (!p.DeliveryLedger.TryGetValue(out var ledger))
        {
            return new HuntInput();
        }

        return new HuntInput
        {
            Kills = ledger.Kills.Select(k => new HuntKill(k.CorpseID, k.PotentialNutrition, k.Tick)).ToList(),
            Butchers = ledger.Butchers.Select(b => new HuntButcher(b.CorpseID, b.MeatNutrition)).ToList(),
        };
    }

    internal static CreditInput FoodCreditInput(ColonyProjection p, DeliveryCredit credit, HuntInput hunting)
    {
        var known = p.DeliveryLedger.TryGetValue(out var ledger);
        var input = new CreditInput { Tick = p.Identity.Tick, Known = known };
        if (!known)
        {
            return input;
        }

        var delivered = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, count) in ledger!.Counts)
        {
            var k = Wire.Name(key.Kind) + ":" + key.SourceID;
            delivered[k] = delivered.GetValueOrDefault(k) + count.Nutrition;
        }

        IReadOnlyList<FoodStock> stocks = [];
        var complete = false;
        if (p.CombinedFoodSupply.TryGetValue(out var supply))
        {
            stocks = supply.Stocks;
            complete = supply.Complete == Known(true);
        }

        delivered[HuntCreditRules.HuntSource] = credit.HuntDelivered(hunting.Kills, hunting.Butchers, stocks, complete);
        return input with { LoadToken = ledger.LoadToken, Delivered = delivered };
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

    internal static bool FoodPlanSupportOf(Fact<FoodPlan> p, CandidateKind kind, string id) => FoodOrdersRules.FoodPlanSupport(p, kind, id);


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
