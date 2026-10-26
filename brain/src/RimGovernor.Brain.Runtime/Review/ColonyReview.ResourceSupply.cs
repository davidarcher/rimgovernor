using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

/// <summary>One short resource: its target, the sources that could cover it and the field plans on offer.</summary>
internal sealed class ResourceSupplyRow
{
    public Resource Resource { get; init; }

    public long Target { get; init; }

    public long Deficit { get; init; }

    public SourceSelection Sel { get; set; }

    public bool SelKnown { get; set; }

    public ResourceMethod Choice { get; set; } = new();

    public List<AcquisitionSource> Open { get; } = [];

    public bool Busy { get; init; }

    public Dictionary<string, FieldPlan> Fields { get; set; } = [];

    public long Standing { get; set; }
}

/// <summary>The resource supply plan with its per-resource rows, built once per review and cached.</summary>
internal sealed class ResourceSupplyState
{
    public ResourceSupply Plan { get; set; } = new();

    public List<Resource> Order { get; } = [];

    public Dictionary<Resource, ResourceSupplyRow> Rows { get; } = [];

    public Dictionary<Resource, long> Derived { get; } = [];

    public int Hunts { get; set; }

    public DeepDrillReading? Drill { get; set; }

    public IReadOnlyList<AcquisitionSource> Acquisitions(Resource resource)
    {
        if (!Rows.TryGetValue(resource, out var row))
        {
            return [];
        }

        var byId = new Dictionary<string, AcquisitionSource>();
        foreach (var source in row.Open)
        {
            byId[source.Id] = source;
        }

        var output = new List<AcquisitionSource>();
        var hunts = Hunts;
        var others = 0;
        foreach (var e in Plan.Opened(resource))
        {
            if (!byId.TryGetValue(e.Candidate.Id, out var source) || !IsAcquisitionKind(e.Candidate.Kind))
            {
                continue;
            }

            if (source.Hunt)
            {
                if (hunts <= 0)
                {
                    continue;
                }

                hunts--;
            }
            else
            {
                if (others == AcquisitionCatalogRules.MaxCatalogSelection)
                {
                    continue;
                }

                others++;
            }

            output.Add(source);
        }

        return output;
    }

    public IReadOnlyList<FieldPlan> OpenedFields()
    {
        var output = new List<FieldPlan>();
        foreach (var resource in Order)
        {
            foreach (var e in Plan.Opened(resource))
            {
                if (Rows[resource].Fields.TryGetValue(e.Candidate.Id, out var plan) && e.Candidate.Kind == CandidateKind.Harvest)
                {
                    output.Add(plan);
                }
            }
        }

        return output;
    }

    public bool FieldRoute(Resource resource)
    {
        if (!Rows.TryGetValue(resource, out var row) || Acquisitions(resource).Count > 0)
        {
            return false;
        }

        foreach (var e in Plan.Opened(resource))
        {
            if (row.Fields.ContainsKey(e.Candidate.Id) && e.Candidate.Kind == CandidateKind.Harvest)
            {
                return true;
            }
        }

        return row.Standing > 0;
    }

    public IReadOnlyDictionary<Resource, long> TradeLines(string trader)
    {
        var output = new Dictionary<Resource, long>();
        foreach (var resource in Rows.Keys)
        {
            foreach (var e in Plan.Opened(resource))
            {
                if (e.Candidate.Kind != CandidateKind.Trade || e.Candidate.Id != TradePolicyRules.TradeCandidateID(trader, resource))
                {
                    continue;
                }

                var units = ResourceRules.PlannedTradeUnits(e);
                output[resource] = output.GetValueOrDefault(resource) + units;
            }
        }

        return output;
    }

    internal static bool IsAcquisitionKind(CandidateKind k) => k is CandidateKind.Chop or CandidateKind.Harvest or CandidateKind.Hunt;
}

internal readonly record struct ResourceSupplyKey(GenerationSnapshot Snapshot, Tick Tick, ulong Generation, ulong Review, string Owner, ulong Offers);

internal static class DeepDrillReadingExtensions
{
    internal static IReadOnlyList<SupplyCandidate> Candidates(this DeepDrillReading? d, Resource resource, long deficit, ResourceSupplyRow row)
    {
        if (d is null)
        {
            return [];
        }

        YieldStorage headroom = row.SelKnown ? new YieldStorage.Accepts(row.Sel.Storage.Capacity) : new YieldStorage.Unread();
        var output = new List<SupplyCandidate>();
        foreach (var place in d.Placeable)
        {
            if (new Resource(place.Site.Definition) != resource)
            {
                continue;
            }

            if (AcquisitionCatalogRules.DeepDrillCandidate(resource, place.Id, DeepDrillRules.AvailableYield(place.Site, deficit), place.Distance, headroom, out var c))
            {
                output.Add(c);
            }
        }

        return output;
    }
}

public sealed partial class ColonyReview
{
    internal async Task<ResourceSupplyState> ResourceSupplyAsync(ControlState state, ReviewState review, NeedOwner goal, CancellationToken call)
    {
        var s = Census;
        var key = new ResourceSupplyKey(state.Snapshot, review.Tick, 0, review.Revision, goal.OwnerKey + ":" + string.Join(",", goal.OpenMethods.Select(m => m.Plan.Value)), TradeOffers.Version());
        lock (s.Mu)
        {
            key = key with { Generation = s.Generation };
            if (s.Supply is not null && s.SupplyKey == key)
            {
                return s.Supply;
            }
        }

        var built = await BuildResourceSupplyAsync(state, review, goal, call).ConfigureAwait(false);
        lock (s.Mu)
        {
            s.Supply = built;
            s.SupplyKey = key;
        }

        return built;
    }

    internal async Task<ResourceSupplyState> BuildResourceSupplyAsync(ControlState state, ReviewState review, NeedOwner goal, CancellationToken call)
    {
        var (expected, projection) = await AcquisitionReadingAsync(state, review, call).ConfigureAwait(false);
        var stock = projection.Facts.ObservedStock();
        var targets = ResourceTargets(state.Snapshot);
        var ranked = ResourceRules.RankResourceTargets(targets, stock).ToList();
        var clothing = Demand.Get(state.Snapshot).Clothing;
        var output = new ResourceSupplyState();
        var planner = ResourceNative is null ? null : ResourcePlanner.NewReviewResourcePlanner(this, ResourceNative);
        var identity = Boundary.Identity(state.Snapshot);
        var undispatched = new HashSet<string>();
        foreach (var method in goal.OpenMethods)
        {
            var plan = Player.Journal.LoadPlan(method.Plan, call);
            foreach (var thing in AcquisitionPlanner.UndispatchedAcquisitions(plan.Progress))
            {
                undispatched.Add(thing);
            }
        }

        var progress = review.ConcernProgress(EconomyFamily.Resource.FilingKey);
        var held = AcquisitionPlanner.CooledSources(projection.Facts.Acquisition, ChopMinGrowth(projection), id => AcquisitionPlanner.AcquisitionCooled(progress, Policy, id, expected.Tick));
        var busy = AcquisitionPlanner.HoldWorked(projection.Facts.Acquisition, undispatched, held);
        var (slots, _) = ResourcePlanner.HuntSlots(projection);
        var hunts = slots;
        output.Hunts = hunts;
        var center = projection.Facts.LayoutPlan?.Center();
        var rows = projection.Facts.Acquisition;
        var traders = projection.Facts.Traders;
        var silver = projection.Facts.Silver();
        var (reserve, reserveKnown) = TradePolicyRules.TradeSilverReserve(projection.Facts.Census.ColonistCount);
        IReadOnlyList<TradeOffers> offers = reserveKnown ? TradeOffers.Fresh(state.Snapshot, expected.Tick, silver, traders) : [];
        var spendable = reserveKnown ? silver : 0;
        if (planner is not null)
        {
            output.Drill = await planner.ReadDeepDrillAsync(state, review, call).ConfigureAwait(false);
        }

        var reach = new RemoteWorkRequest();
        var reachRead = false;
        var fieldPlanner = NewResourceFieldPlanner(state, review, expected, projection, call);
        IReadOnlyList<ResourceRunway> runways = review.Enabled && review.Snapshot == state.Snapshot ? review.ResourceRunwayState() : [];
        var inputs = new List<ResourceSupplyInput>();
        var queued = new HashSet<Resource>(ranked.Select(t => t.Resource));
        var reader = StockReader.Of(stock);
        for (var i = 0; i < ranked.Count; i++)
        {
            var target = ranked[i];
            var resource = target.Resource;
            var deficit = ResourceRules.OutstandingTarget(target.Target, reader.Count(resource));
            if (resource.Value == "Beer" || deficit <= 0)
            {
                continue;
            }

            var row = new ResourceSupplyRow { Resource = resource, Target = target.Target, Deficit = deficit, Busy = busy.Contains(resource.Value) };
            output.Order.Add(resource);
            output.Rows[resource] = row;
            var input = new ResourceSupplyInput { Resource = resource, Deficit = deficit };
            var candidates = new List<SupplyCandidate>();
            if (clothing.Contains(resource))
            {
                input = input with { HorizonDays = ClothingDemandRules.ClothingHorizonDays };
            }

            if (planner is not null)
            {
                if (!reachRead)
                {
                    reach = await planner.MiningReachAsync(state, review.Tick, call).ConfigureAwait(false);
                    reachRead = true;
                }

                var (choice, ingredients) = await planner.MethodChoiceAsync(identity, resource, target.Target, call).ConfigureAwait(false);
                row.Choice = choice;
                input = input with { Usable = [.. AcquisitionCatalogRules.UsableIngredients(runways, ingredients)] };
                (row.Sel, row.SelKnown) = await planner.SourcesForDeficitAsync(identity, resource, target.Target, stock, reach, call).ConfigureAwait(false);
                if (row.SelKnown)
                {
                    candidates.AddRange(AcquisitionCatalogRules.MineCandidates(resource, row.Sel.Selected ?? [], new YieldStorage.Accepts(row.Sel.Storage.Capacity)));
                }

                if (AcquisitionCatalogRules.ProduceCandidate(row.Choice, deficit, out var produce))
                {
                    candidates.Add(produce);
                    foreach (var cost in produce.UpfrontCost.Resources)
                    {
                        var (shortfall, known) = ResourceRules.IngredientShortfall(cost, input.Usable);
                        if (known && shortfall > 0 && !queued.Contains(cost.Key.Def))
                        {
                            queued.Add(cost.Key.Def);
                            output.Derived[cost.Key.Def] = ResourceRules.DerivedTarget(reader.Count(cost.Key.Def), shortfall);
                            ranked.Add(new ResourceTarget { Resource = cost.Key.Def, Target = output.Derived[cost.Key.Def] });
                        }
                    }
                }
            }

            var (need, standing, open, priced) = ResourceRules.OutstandingSupply(resource, deficit, rows, clothing, held, output.Hunts, fieldPlanner.Standing(resource));
            row.Standing = standing;
            row.Open.AddRange(open);
            if (need > 0 && fieldPlanner.Serves(resource))
            {
                var (candidate, plan) = fieldPlanner.Candidate(resource, need);
                if (candidate is not null)
                {
                    candidates.Add(candidate);
                    row.Fields = new Dictionary<string, FieldPlan> { [candidate.Id] = plan };
                    if (candidate.Lead is CandidateLead.Days { Value: var lead } && lead > input.HorizonDays)
                    {
                        input = input with { HorizonDays = lead };
                    }
                }
            }

            input = input with { Deficit = ResourceRules.UncoveredSupply(need) };
            if (center is { } at)
            {
                candidates.AddRange(AcquisitionCatalogRules.AcquisitionSourceCandidates(resource, priced, at, new YieldStorage.Accepts(need)));
            }

            candidates.AddRange(output.Drill.Candidates(resource, deficit, row));
            candidates.AddRange(TradePolicyRules.TradeOfferCandidates(resource, offers, deficit, spendable, reserve));
            input = input with { Candidates = [.. candidates] };
            inputs.Add(input);
        }

        try
        {
            var labor = ResourcePlanner.ResourceLabor(projection);
            output.Plan = ResourceRules.PlanResourceSupply(inputs, labor);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("buildResourceSupply", e);
        }

        return output;
    }
}

public sealed partial class ResourcePlanner
{
    /// <summary>The labor the food plan leaves the resource supply; the planner needs it read (RequiredFactException).</summary>
    internal static double ResourceLabor(ColonyReading p)
    {
        var workers = ResearchRules.Workers(p.Facts.ForecastObserved.WorkPawns.Pawns);
        return ResourceRules.ResidualSupplyLabor(workers, p.Facts.FoodPlan ?? throw new RequiredFactException("food plan"));
    }

    internal static (int Slots, bool NoHunter) HuntSlots(ColonyReading projection)
    {
        int slots;
        var pawns = projection.Facts.ForecastObserved.WorkPawns.Pawns;
        {
            var profiles = PawnRules.Profiles(pawns);
            if (PawnRules.HunterFor(profiles) is null)
            {
                return (0, true);
            }

            slots = PawnRules.HuntBudget(profiles, checked((int)projection.Snapshot.PendingHunts));
        }

        return (slots, false);
    }
}
