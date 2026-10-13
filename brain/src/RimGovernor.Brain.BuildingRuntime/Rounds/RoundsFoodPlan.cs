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

    /// <summary>The cached food plan for the projection; recomputed when the census generation, the offers, the seasonal thresholds or the observed identity moved.</summary>
    internal Fact<FoodPlan> FoodPlanOf(ColonyProjection p)
    {
        var s = Census;
        lock (s.Mu)
        {
            var seasonal = Seasonal(p.Facts);
            var trade = FoodTradeOf(p);
            if (s.FoodPlan.IsKnown && s.FoodGeneration == s.Generation && s.FoodOffers == trade.Revision
                && s.FoodMin == seasonal.FoodMinDays && s.FoodTarget == seasonal.FoodTargetDays && SameObservedIdentity(s.FoodIdentity, p.Identity))
            {
                return s.FoodPlan;
            }

            var plan = ReviewFoodPlan(p, Policy, FoodCredit, HuntAdmission, trade);
            if (plan.TryGetValue(out var v))
            {
                HuntAdmission.Admit(PolicyFunctions.HuntRequest(Known(v)));
            }

            LogFoodCredit(FoodCredit.Drain());
            if (plan.TryGetValue(out v) && FoodGapZero)
            {
                plan = Known(v with { GapPerDay = 0 });
            }

            if (plan.IsKnown)
            {
                s.FoodIdentity = p.Identity;
                s.FoodPlan = plan;
                s.FoodGeneration = s.Generation;
                s.FoodMin = seasonal.FoodMinDays;
                s.FoodTarget = seasonal.FoodTargetDays;
                s.FoodOffers = trade.Revision;
            }

            return plan;
        }
    }

    internal FoodTrade FoodTradeOf(ColonyProjection p)
    {
        var tk = p.Facts.Traders.TryGetValue(out var traders);
        var sk = p.Facts.Silver().TryGetValue(out var silver);
        var rk = PolicyFunctions.TradeSilverReserve(p.Facts.Colonists).TryGetValue(out var reserve);
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

        var marked = traders.Select(t => t with { Unpriced = t.CanTrade && !priced.Contains(t.Id) }).ToList();
        p = p with { Facts = p.Facts with { Traders = Known<IReadOnlyList<Trader>>(marked) } };
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static Fact<FoodPlan> ReviewFoodPlan(ColonyProjection p, RoundsPolicy thresholds, DeliveryCredit credit, HuntAdmission hunt, FoodTrade trade)
    {
        if (!p.CombinedFoodSupply.TryGetValue(out var supply) || !p.Acquisition.TryGetValue(out var sources))
        {
            return Unknown<FoodPlan>();
        }

        var wk = p.Workers.TryGetValue(out var workers);
        if (p.WorkPawns.TryGetValue(out var workPawns))
        {
            wk = PolicyFunctions.RoundsWorkers(workPawns).TryGetValue(out workers);
        }

        if (!wk)
        {
            return Unknown<FoodPlan>();
        }

        FoodForecast forecast;
        try
        {
            forecast = PolicyFunctions.ForecastFood(supply, null);
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
            var profiles = PolicyFunctions.Profiles(pawns);
            gunners = PolicyFunctions.SquadGunners(profiles);
            hunters = PolicyFunctions.Hunters(profiles);
        }

        var weatherAccuracy = Unknown<double>();
        if (p.Environment.TryGetValue(out var env))
        {
            weatherAccuracy = env.WeatherAccuracy;
        }

        var channels = new List<SupplyCandidate>();
        channels.AddRange(PolicyFunctions.ForageChannels(sources));
        channels.AddRange(PolicyFunctions.HuntCandidates(sources, gunners, weatherAccuracy));
        channels.AddRange(PolicyFunctions.HuntPrerequisiteCandidates(p.HuntHolds));
        var hunting = FoodHuntInput(p);
        channels = PolicyFunctions.HuntThroughput(channels, hunters, PolicyFunctions.HuntCadence(hunting.Kills, hunters, p.Identity.Tick.Value)).ToList();
        if (p.ProductionBenches.TryGetValue(out var benches) && p.FoodSupply.TryGetValue(out human))
        {
            var ids = human.Consumers.Select(c => c.Id).ToList();
            if (PolicyFunctions.HumanFoodChannel(benches, supply, ids, thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions).FoodTargetDays, p.Facts.IdeologyRead()) is { } channel)
            {
                channels.Add(channel);
            }
        }

        if (p.FoodChannels.TryGetValue(out var census) && census.FishableWater.TryGetValue(out var water))
        {
            var regions = water.Regions.Select(region => new FishingRegion
            {
                Id = PolicyFunctions.FishingRegionId(region.Root),
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
                channels.AddRange(PolicyFunctions.FishingChannels(request));
            }
            catch (DomainException)
            {
                return Unknown<FoodPlan>();
            }
        }

        if (p.FoodChannels.TryGetValue(out var animals))
        {
            channels.AddRange(PolicyFunctions.AnimalProductChannels(PolicyFunctions.WithFeed(animals.AnimalProducts(), p.Facts.AnimalUpkeep.Animals)));
        }

        channels.AddRange(PolicyFunctions.StockIngredientChannels(supply));
        if (p.FoodFields.TryGetValue(out var fields))
        {
            channels.AddRange(PolicyFunctions.CropChannels(
                fields,
                new CropKitchen { Benches = p.ProductionBenches, Cooks = Known((double)workers) },
                new CropSeason { Calendar = p.Facts.Calendar, Conditions = p.Facts.DisasterConditions }));
        }

        var (field, _) = FieldRequest(p, thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions).FoodTargetDays);
        channels.AddRange(PolicyFunctions.NewFieldChannels(field, new CropKitchen { Benches = p.ProductionBenches, Cooks = Known((double)workers) }));
        (CandidateKind Kind, string Id)[] supports = [(CandidateKind.Cook, "cooking-capacity"), (CandidateKind.Reserve, "stock-protection")];
        foreach (var support in supports)
        {
            channels.Add(PolicyFunctions.FoodSupportCandidate(support.Kind, support.Id));
        }

        channels = hunt.Apply(channels).ToList();
        channels = credit.Apply(channels, FoodCreditInput(p, credit, hunting)).ToList();
        var seasonal = thresholds.Seasonal(p.Facts.Calendar, p.Facts.DisasterConditions);
        FoodPlan plan;
        try
        {
            plan = PolicyFunctions.SupplyFoodPlan(new FoodPlanRequest
            {
                Demand = forecast,
                MinDays = seasonal.FoodMinDays,
                TargetDays = seasonal.FoodTargetDays,
                EmergencyDays = seasonal.FootholdFoodDays,
                Channels = Known<IReadOnlyList<SupplyCandidate>>(channels),
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
                offers.AddRange(PolicyFunctions.SlaughterFoodChannels(animals.Slaughter, p.Facts.AnimalUpkeep.Animals, HerdPolicyOf(p.Facts, plan)));
            }

            offers.AddRange(PolicyFunctions.TradeFoodChannels(trade.Offers, trade.Silver, trade.Reserve, plan.GapPerDay * plan.HorizonDays));
            var acquire = new AnimalAcquisition
            {
                Races = p.Facts.AnimalUpkeep.AnimalRaces,
                Owned = p.Facts.AnimalUpkeep.Animals,
                Wild = p.Facts.AnimalUpkeep.WildAnimals,
                Herd = HerdPolicyOf(p.Facts, plan),
                Handlers = p.Facts.WorkProfiles,
            };
            offers.AddRange(PolicyFunctions.TameFoodChannels(acquire));
            offers.AddRange(PolicyFunctions.AnimalPurchaseFoodChannels(trade.Offers, acquire, trade.Silver, trade.Reserve));
            if (offers.Count > 0)
            {
                channels.AddRange(offers);
                try
                {
                    plan = PolicyFunctions.SupplyFoodPlan(new FoodPlanRequest
                    {
                        Demand = forecast,
                        MinDays = seasonal.FoodMinDays,
                        TargetDays = seasonal.FoodTargetDays,
                        EmergencyDays = seasonal.FootholdFoodDays,
                        Channels = Known<IReadOnlyList<SupplyCandidate>>(channels),
                        Labor = Known(workers * 20000.0),
                    });
                }
                catch (DomainException)
                {
                    return Unknown<FoodPlan>();
                }
            }
        }

        var herd = HerdPolicyOf(p.Facts, plan);
        var portfolio = plan.Portfolio.Select(e =>
        {
            if (e.Channel.Kind != CandidateKind.AnimalProduct)
            {
                return e;
            }

            herd.PopulationMin.TryGetValue(new Resource(e.Channel.Id), out var floor);
            return e with
            {
                Terms = [.. e.Terms, new CandidateTerm { Name = "effective_herd_floor", Value = floor }],
                Reason = e.Reason + $"; MaintainHerd-{e.Channel.Id} floor {floor}",
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
            Kills = ledger.Kills.Select(k => new HuntKill { CorpseId = k.CorpseId, Potential = k.PotentialNutrition, Tick = k.Tick }).ToList(),
            Butchers = ledger.Butchers.Select(b => new HuntButcher { CorpseId = b.CorpseId, MeatNutrition = b.MeatNutrition }).ToList(),
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
            var k = key.Kind.WireName() + ":" + key.SourceId;
            delivered[k] = delivered.GetValueOrDefault(k) + count.Nutrition;
        }

        IReadOnlyList<FoodStock> stocks = [];
        var complete = false;
        if (p.CombinedFoodSupply.TryGetValue(out var supply))
        {
            stocks = supply.Stocks;
            complete = supply.Complete == Known(true);
        }

        delivered[PolicyFunctions.HuntSource] = credit.HuntDelivered(hunting.Kills, hunting.Butchers, stocks, complete);
        return input with { LoadToken = ledger.LoadToken, Delivered = delivered };
    }

    internal static void LogFoodCredit(IReadOnlyList<CreditChange> changes)
    {
        foreach (var c in changes)
        {
            Tel.Decide(CancellationToken.None, FoodCreditDecision(c));
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

    internal static bool FoodPlanSupportOf(Fact<FoodPlan> p, CandidateKind kind, string id) => PolicyFunctions.FoodPlanSupport(p, kind, id);

    /// <summary>Whether the open food plan still has room for another field: the gap minus what open field work will yield stays positive.</summary>
    internal static bool FoodPlanFieldRoom(ColonyProjection p, IReadOnlyList<PlanState> plans)
    {
        if (!p.Facts.FoodPlan.TryGetValue(out var plan) || plan.GapPerDay <= 0 || !FoodPlanOpensField(plan))
        {
            return false;
        }

        var gap = plan.GapPerDay;
        foreach (var existing in plans)
        {
            foreach (var progress in existing.Progress)
            {
                if (!Standard.StandardWorkOpen([progress]))
                {
                    continue;
                }

                string crop;
                int cells;
                if (progress.Action.ZoneCreate is { } zone)
                {
                    crop = zone.Crop;
                    cells = zone.Cells.Count;
                }
                else if (progress.Action.ZoneCellEdit is { } edit && edit.Mode == ZoneCellEditMode.AddZoneCells)
                {
                    crop = "";
                    foreach (var farm in p.Farms)
                    {
                        if (farm.Id == edit.Zone)
                        {
                            crop = farm.Crop;
                        }
                    }

                    if (crop.Length == 0)
                    {
                        continue;
                    }

                    cells = edit.Cells.Count;
                }
                else
                {
                    if (progress.Action.Building is { } b && !IsShellMethod(existing.Method) && !ButcherBenchDefinition(b.Definition))
                    {
                        return false;
                    }

                    continue;
                }

                var found = false;
                foreach (var d in p.Definitions)
                {
                    if (d.Name != crop)
                    {
                        continue;
                    }

                    var yk = d.HarvestNutrition.TryGetValue(out var yield);
                    var dk = d.GrowDays.TryGetValue(out var days);
                    if (!yk || !dk || yield < 0 || days <= 0)
                    {
                        return false;
                    }

                    gap -= yield * cells / days;
                    found = true;
                    break;
                }

                if (!found)
                {
                    return false;
                }
            }
        }

        return gap > 0;
    }

    internal static bool FoodPlanOpensField(FoodPlan plan)
    {
        foreach (var entry in plan.Portfolio)
        {
            if (entry.Channel.Kind == CandidateKind.Crop && entry.Channel.Id.StartsWith(PolicyFunctions.NewFieldPrefix, StringComparison.Ordinal) && entry.Decision == FoodPlanDecision.Open)
            {
                return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<PawnId> FoodConsumerIds(FoodSupply supply) => supply.Consumers.Select(c => c.Id).ToList();
}
