namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>One short resource: its target, the sources that could cover it and the field plans on offer.</summary>
internal sealed class ResourceSupplyRow
{
    public Resource Resource { get; init; }

    public long Target { get; init; }

    public long Deficit { get; init; }

    public SourceSelection Sel { get; set; }

    public bool SelKnown { get; set; }

    public ResourceMethod Choice { get; set; }

    public List<AcquisitionSource> Open { get; } = [];

    public bool Busy { get; init; }

    public Dictionary<string, FieldPlan> Fields { get; set; } = [];

    public long Standing { get; set; }
}

/// <summary>The resource supply plan with its per-resource rows, built once per review and cached.</summary>
internal sealed class ResourceSupplyState
{
    public ResourceSupplyPlan Plan { get; set; }

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
                if (others == PolicyFunctions.MaxCatalogSelection)
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
                if (e.Candidate.Kind != CandidateKind.Trade || e.Candidate.Id != PolicyFunctions.TradeCandidateId(trader, resource))
                {
                    continue;
                }

                e.Candidate.Yields[0].StockCap.TryGetValue(out var units);
                if (e.Wanted > 0)
                {
                    units = Math.Min(units, e.Wanted);
                }

                output[resource] = output.GetValueOrDefault(resource) + units;
            }
        }

        return output;
    }

    internal static bool IsAcquisitionKind(CandidateKind k) => k is CandidateKind.Chop or CandidateKind.Harvest or CandidateKind.Hunt;
}

internal readonly record struct ResourceSupplyKey(GenerationSnapshot Snapshot, Tick Tick, ulong Generation, ulong Review, ulong Standard, ulong Offers);

internal static class DeepDrillReadingExtensions
{
    internal static IReadOnlyList<SupplyCandidate> Candidates(this DeepDrillReading? d, Resource resource, long deficit, ResourceSupplyRow row)
    {
        if (d is null)
        {
            return [];
        }

        var headroom = row.SelKnown ? Known(row.Sel.Storage.Capacity) : Unknown<long>();
        var output = new List<SupplyCandidate>();
        foreach (var place in d.Placeable)
        {
            if (new Resource(place.Site.Definition) != resource)
            {
                continue;
            }

            var (c, ok) = PolicyFunctions.DeepDrillCandidate(resource, place.Id, Math.Min(place.Site.Count, deficit), place.Distance, headroom);
            if (ok)
            {
                output.Add(c);
            }
        }

        return output;
    }
}

public sealed partial class Rounder
{
    internal ResourceSupplyState ResourceSupply(CancellationToken call, ControlState state, Rounds review, StandardState goal)
    {
        var s = Census;
        var key = new ResourceSupplyKey(state.Snapshot, review.Tick, 0, review.Revision, goal.Revision, TradeOffers.Version());
        lock (s.Mu)
        {
            key = key with { Generation = s.Generation };
            if (s.Supply is not null && s.SupplyKey == key)
            {
                return s.Supply;
            }
        }

        var built = BuildResourceSupply(call, state, review, goal);
        lock (s.Mu)
        {
            s.Supply = built;
            s.SupplyKey = key;
        }

        return built;
    }

    internal ResourceSupplyState BuildResourceSupply(CancellationToken call, ControlState state, Rounds review, StandardState goal)
    {
        var (expected, projection) = AcquisitionReading(call, state, review);
        var stock = projection.Facts.Resources;
        var targets = ResourceTargets(state.Snapshot);
        var ranked = PolicyFunctions.RankResourceTargets(targets, stock).ToList();
        var serves = Demand.Get(state.Snapshot).Serves;
        var output = new ResourceSupplyState();
        var planner = ResourceNative is null ? null : RoundsResourcePlanner.NewRoundsResourcePlanner(this, ResourceNative);
        var identity = BoundaryPackage.Identity(state.Snapshot);
        var undispatched = new HashSet<string>();
        foreach (var method in goal.Methods)
        {
            var plan = Player.Journal.LoadPlan(call, method.Plan);
            foreach (var thing in BuildingRuntimePackage.UndispatchedAcquisitions(plan.Progress))
            {
                undispatched.Add(thing);
            }
        }

        var progress = review.ConcernProgress(ConcernId.MaintainResource);
        var held = BuildingRuntimePackage.CooledSources(projection.Acquisition, ChopMinGrowth(projection), id => BuildingRuntimePackage.AcquisitionCooled(progress, Policy, id, expected.Tick));
        var busy = BuildingRuntimePackage.HoldWorked(projection.Acquisition, undispatched, held);
        var (slots, _) = RoundsResourcePlanner.HuntSlots(projection);
        slots.TryGetValue(out var hunts);
        output.Hunts = hunts;
        var centered = projection.Center().TryGetValue(out var center);
        projection.Acquisition.TryGetValue(out var rows);
        rows ??= [];
        IReadOnlyList<TradeOffers> offers = [];
        long spendable = 0;
        long reserve = 0;
        if (projection.Facts.Traders.TryGetValue(out var traders))
        {
            var silverKnown = projection.Facts.Silver().TryGetValue(out var silver);
            var (reserveValue, reserveKnown) = PolicyFunctions.TradeSilverReserve(projection.Facts.Colonists);
            reserve = reserveValue;
            if (silverKnown && reserveKnown)
            {
                offers = TradeOffers.Fresh(state.Snapshot, expected.Tick, silver, traders);
                spendable = silver;
            }
        }

        if (planner is not null)
        {
            output.Drill = planner.DeepDrillReading(call, state, review);
        }

        RemoteWorkRequest reach = default;
        var reachRead = false;
        var fieldPlanner = NewResourceFieldPlanner(call, state, review, expected, projection);
        IReadOnlyList<ResourceRunway> runways = review.Enabled && review.Snapshot == state.Snapshot ? review.ResourceRunwayState() : [];
        var inputs = new List<ResourceSupplyInput>();
        var queued = new HashSet<Resource>(ranked.Select(t => t.Resource));
        var reader = new StockReader { Resources = stock };
        for (var i = 0; i < ranked.Count; i++)
        {
            var target = ranked[i];
            var resource = target.Resource;
            var deficit = target.Target - reader.Units(resource);
            if (resource.Value == "Beer" || deficit <= 0)
            {
                continue;
            }

            var row = new ResourceSupplyRow { Resource = resource, Target = target.Target, Deficit = deficit, Busy = busy.Contains(resource.Value) };
            output.Order.Add(resource);
            output.Rows[resource] = row;
            var input = new ResourceSupplyInput { Resource = resource, Deficit = deficit };
            var candidates = new List<SupplyCandidate>();
            if (serves.TryGetValue(resource, out var servedBy) && servedBy == resource)
            {
                input.HorizonDays = PolicyFunctions.ClothingHorizonDays;
            }

            if (planner is not null)
            {
                if (!reachRead)
                {
                    reach = planner.MiningReach(call, state, review.Tick);
                    reachRead = true;
                }

                var (choice, ingredients) = planner.MethodChoice(call, identity, resource, target.Target);
                row.Choice = choice;
                input.Usable = PolicyFunctions.UsableIngredients(runways, ingredients);
                (row.Sel, row.SelKnown) = planner.SourcesForDeficit(call, identity, resource, target.Target, stock, reach);
                if (row.SelKnown)
                {
                    candidates.AddRange(PolicyFunctions.MineCandidates(resource, row.Sel.Selected ?? [], Known(row.Sel.Storage.Capacity)));
                }

                var (produce, found) = PolicyFunctions.ProduceCandidate(row.Choice, deficit);
                if (found)
                {
                    candidates.Add(produce);
                    foreach (var cost in produce.UpfrontCost.Resources)
                    {
                        var shortfall = cost.Count;
                        foreach (var usable in input.Usable)
                        {
                            if (usable.Key == cost.Key)
                            {
                                shortfall -= usable.Count;
                            }
                        }

                        var known = input.Usable.Any(q => q.Key == cost.Key);
                        if (known && shortfall > 0 && !queued.Contains(cost.Key.Def))
                        {
                            queued.Add(cost.Key.Def);
                            output.Derived[cost.Key.Def] = reader.Units(cost.Key.Def) + shortfall;
                            ranked.Add(new ResourceTarget { Resource = cost.Key.Def, Target = output.Derived[cost.Key.Def] });
                        }
                    }
                }
            }

            var need = deficit;
            var priced = new List<AcquisitionSource>();
            foreach (var source in rows)
            {
                var (offered, serving) = PolicyFunctions.ResourceSourceFor(source, resource, serves);
                if (!serving)
                {
                    continue;
                }

                if (source.Designated)
                {
                    need -= (long)Math.Round(offered.Yield, MidpointRounding.AwayFromZero);
                }
                else if (!held.Contains(source.Id) && !(source.Hunt && output.Hunts <= 0))
                {
                    row.Open.Add(source);
                    priced.Add(offered);
                }
            }

            row.Standing = Math.Max(fieldPlanner.Standing(resource) - BuildingRuntimePackage.GrownPlantationYield(rows, resource), 0);
            need -= row.Standing;
            if (need > 0 && fieldPlanner.Serves(resource))
            {
                var (candidate, plan, ok) = fieldPlanner.Candidate(resource, need);
                if (ok)
                {
                    candidates.Add(candidate);
                    row.Fields = new Dictionary<string, FieldPlan> { [candidate.Id] = plan };
                    candidate.LeadDays.TryGetValue(out var lead);
                    if (lead > input.HorizonDays)
                    {
                        input.HorizonDays = lead;
                    }
                }
            }

            input.Deficit = Math.Max(need, 0);
            if (centered)
            {
                candidates.AddRange(PolicyFunctions.AcquisitionSourceCandidates(resource, priced, center, Known(need)));
            }

            candidates.AddRange(output.Drill.Candidates(resource, deficit, row));
            candidates.AddRange(PolicyFunctions.TradeOfferCandidates(resource, offers, deficit, spendable, reserve));
            input.Candidates = candidates;
            inputs.Add(input);
        }

        try
        {
            var labor = RoundsResourcePlanner.ResourceLabor(projection);
            output.Plan = PolicyFunctions.PlanResourceSupply(inputs, labor);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("buildResourceSupply", e);
        }

        PlanTradeAcquisition(call, state.Snapshot, "resources", projection, output.Plan.Plan.Demands, null);
        return output;
    }
}

public sealed partial class RoundsResourcePlanner
{
    internal static Fact<double> ResourceLabor(ColonyProjection p)
    {
        var workers = p.Workers;
        if (p.WorkPawns.TryGetValue(out var pawns))
        {
            workers = PolicyFunctions.RoundsWorkers(pawns);
        }

        return PolicyFunctions.ResidualSupplyLabor(workers, p.Facts.FoodPlan);
    }

    internal static (Fact<int> Slots, bool NoHunter) HuntSlots(ColonyProjection projection)
    {
        var slots = Unknown<int>();
        var pendingKnown = projection.PendingHunts.TryGetValue(out var pending);
        if (projection.WorkPawns.TryGetValue(out var pawns))
        {
            var profiles = PolicyFunctions.Profiles(pawns);
            var (_, ok) = PolicyFunctions.HunterFor(profiles);
            if (!ok)
            {
                return (Known(0), true);
            }

            if (pendingKnown)
            {
                slots = Known(PolicyFunctions.HuntBudget(profiles, pending!));
            }
        }

        return (slots, false);
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static long GrownPlantationYield(IReadOnlyList<AcquisitionSource> rows, Resource resource)
    {
        var units = 0.0;
        foreach (var s in rows)
        {
            if (s.Plantation && s.Tree && new Resource(s.Resource) == resource)
            {
                units += s.Yield;
            }
        }

        return (long)Math.Round(units, MidpointRounding.AwayFromZero);
    }
}
