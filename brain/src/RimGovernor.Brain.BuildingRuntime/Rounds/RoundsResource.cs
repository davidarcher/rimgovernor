namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsResourceSource : IColonySource
{
    (IReadOnlyList<GearBenchRead> Benches, BridgeResult Result) ReadGearBenches(CancellationToken ct, C.Identity identity);

    (IReadOnlyList<Stock> Stock, BridgeResult Result) ReadSupplyStock(CancellationToken ct, C.Identity identity, IReadOnlyList<string> names);

    (IReadOnlyList<ResourceSource> Sources, ResourceStorage Storage, BridgeResult Result) ReadResourceSources(CancellationToken ct, C.Identity identity, string resource);

    (OP.ZonePreviewReply Reply, BridgeResult Result) PreviewZone(CancellationToken ct, C.Identity identity, ZoneCreate zone);
}

/// <summary>What the resource planner decided: a plan, a wait, or the sources it settled on.</summary>
public readonly record struct RoundsResourceResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }

    public uint NativeWorkTicks { get; init; }

    public IReadOnlyList<ResourceSource> Sources { get; init; }

    /// <summary>True when the resource went nowhere that would consume its turn: claimed, used, held without a source, or awaiting a source.</summary>
    internal bool Undispatched()
    {
        var v = Verdict;
        return v.Is(Cause.MethodUsed) || v.Is(Cause.Claim) || v.Is(Cause.HeldNoSource) || (v.Is(Cause.AwaitingPlan) && v.Refusal.Subject == RoundsResourcePlanner.ResourceSourceSubject);
    }
}

internal record struct SourceSelection
{
    public IReadOnlyList<ResourceSource> Selected { get; set; }

    public ResourceStorage Storage { get; set; }

    public bool Designated { get; set; }

    public bool None { get; set; }
}

/// <summary>Composes MaintainResource: ranks the resource targets and mines, produces or tunnels toward the first one short.</summary>
public sealed partial class RoundsResourcePlanner
{
    internal const string ResourceSourceSubject = "resource_source";

    private readonly Rounder reviewer;
    private readonly IRoundsResourceSource native;

    private RoundsResourcePlanner(Rounder reviewer, IRoundsResourceSource native)
    {
        this.reviewer = reviewer;
        this.native = native;
    }

    public static RoundsResourcePlanner NewRoundsResourcePlanner(Rounder reviewer, IRoundsResourceSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsResourcePlanner: reviewer == null || native == null");
        }

        reviewer.ResourceNative = native;
        return new RoundsResourcePlanner(reviewer, native);
    }

    internal static Fact<IReadOnlyList<Amount>> ResourceStockFacts(O.ColonyFactsSnapshot v)
    {
        if (BuildingRuntimePackage.MedicalIssue(v.Issues, "resources"))
        {
            return Unknown<IReadOnlyList<Amount>>();
        }

        var rows = new List<Amount>();
        foreach (var q in v.Resources)
        {
            if (q.Units is null)
            {
                return Unknown<IReadOnlyList<Amount>>();
            }

            rows.Add(new Amount(new Resource(q.DefName), q.Units.Value));
        }

        return Known<IReadOnlyList<Amount>>(rows);
    }

    internal static Verdict NoResourceSource(Resource resource) => BuildingRuntimePackage.AwaitingPlan(ResourceSourceSubject, resource.Value);

    internal RoundsResourceResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsResourceResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var targets = reviewer.ResourceTargets(state.Snapshot);
        if (targets.Count == 0)
        {
            return new RoundsResourceResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsResourceResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (recordedCall, recorded) = BuildingRuntimePackage.RecordPlannerStep(call, ConcernId.MaintainResource, state.Snapshot, review.Tick);
        call = recordedCall;
        using var recordedScope = recorded;
        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.MaintainResource);
        if (!workable)
        {
            return new RoundsResourceResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        BuildingRuntimePackage.CancelStalledExcavation(call, p.Journal, goal, review.Tick);
        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(call, method.Plan);
            if (!StorePackage.PlanOpen(plan))
            {
                continue;
            }

            var requestOnly = plan.Spec.Actions.Count != 0;
            foreach (var action in plan.Spec.Actions)
            {
                requestOnly = requestOnly && action.Kind == ActionKind.CommsTradeRequest;
            }

            if (requestOnly)
            {
                continue;
            }

            return new RoundsResourceResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
        }

        var started = reviewer.Clock.Now();
        var identity = Boundary.Identity(state.Snapshot);
        var (reply, _) = native.ReadColonyFacts(call, identity, false);
        var observed = reply.Observed ?? throw new ControlException("step: observed == null");
        try
        {
            BridgePackage.ValidateColonyFacts(observed, identity);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: colony facts invalid", e);
        }

        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: colony facts context", e);
        }

        if (observed.Context.Tick < review.Tick.Value)
        {
            throw new ControlException("step: colony facts older than the review");
        }

        var stock = ResourceStockFacts(observed);
        var (drillResult, drillHandled) = DeepDrill(call, epoch, state, goal, review, started);
        if (drillHandled)
        {
            return drillResult;
        }

        targets = reviewer.ResourceTargets(state.Snapshot);
        var supply = reviewer.ResourceSupply(call, state, review, goal);
        var ranked = PolicyFunctions.RankResourceTargets(PolicyFunctions.ResourceConcernTargets(targets, supply.Derived), stock);
        var have = new StockReader { Resources = stock };
        RoundsResourceResult? first = null;
        var sourceless = new List<Resource>();
        foreach (var row in ranked)
        {
            RoundsResourceResult result;
            if (row.Resource.Value == "Beer")
            {
                result = DispatchResourceConcern(call, epoch, state, goal, review.Tick, identity, row.Resource, row.Target, stock, started);
            }
            else if (have.Units(row.Resource) >= row.Target)
            {
                continue;
            }
            else
            {
                result = DispatchSupplied(call, epoch, state, goal, review.Tick, identity, supply, row.Resource, stock, started);
            }

            if (!result.Undispatched() || result.Plan != default || result.NativeWorkTicks != 0)
            {
                return result;
            }

            if (result.Verdict.Is(Cause.HeldNoSource))
            {
                sourceless.Add(row.Resource);
            }

            first ??= result;
        }

        if (first is null)
        {
            return new RoundsResourceResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        var chosen = first.Value;
        if (sourceless.Count > 0)
        {
            chosen = chosen with { Verdict = BuildingRuntimePackage.NoSource([.. sourceless]) };
        }

        return chosen;
    }

    internal (ResourceMethod Choice, IReadOnlyList<Stock> Supply) MethodChoice(CancellationToken call, C.Identity identity, Resource resource, long target)
    {
        var (census, _) = native.ReadGearBenches(call, identity);
        var benches = census.Select(row => row.Bench).ToList();
        IReadOnlyList<Stock> supply = [];
        var names = RecipeIngredientNames(census, resource);
        if (names.Count > 0)
        {
            (supply, _) = native.ReadSupplyStock(call, identity, names);
        }

        var request = new ResourceMethodRequest { Resource = resource, Target = target, Benches = Known<IReadOnlyList<GearBench>>(benches), Stock = supply };
        SnapshotPackage.NoteResourceMethod(request);
        return (PolicyFunctions.SelectResourceMethod(request), supply);
    }

    internal RoundsResourceResult DispatchResourceConcern(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, Tick reviewTick, C.Identity identity, Resource resource, long target, Fact<IReadOnlyList<Amount>> stock, DateTimeOffset started)
    {
        if (resource.Value == "Beer")
        {
            var items = reviewer.ItemFacts(call, state.Snapshot);
            if (items.Wort.Value == "")
            {
                throw new ControlException("dispatchResourceConcern: the catalog names no wort def");
            }

            resource = items.Wort;
        }

        var (choice, _) = MethodChoice(call, identity, resource, target);
        switch (choice.Kind)
        {
            case ResourceMethodKind.Produce:
            case ResourceMethodKind.Wait:
                return new RoundsResourceResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks };
        }

        var (result, _) = AcquireFromSources(call, epoch, state, goal, reviewTick, identity, resource, target, stock, started, null);
        return result;
    }

    internal RoundsResourceResult DispatchSupplied(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, Tick reviewTick, C.Identity identity, ResourceSupplyState supply, Resource resource, Fact<IReadOnlyList<Amount>> stock, DateTimeOffset started)
    {
        if (!supply.Rows.TryGetValue(resource, out var row) || row is null)
        {
            return new RoundsResourceResult { Verdict = NoResourceSource(resource) };
        }

        if (!row.SelKnown && row.Choice.Kind != ResourceMethodKind.Produce)
        {
            return new RoundsResourceResult { Verdict = NoResourceSource(resource) };
        }

        var mines = supply.Plan.OpenedIds(resource, CandidateKind.Mining);
        foreach (var e in supply.Plan.Opened(resource))
        {
            switch (e.Candidate.Kind)
            {
                case CandidateKind.Produce:
                    return new RoundsResourceResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks };
                case CandidateKind.Mining:
                {
                    var selection = row.Sel;
                    selection.Selected = row.Sel.Selected.Where(source => mines.Contains(source.ThingId)).ToList();
                    var (result0, dispatched) = AcquireFromSources(call, epoch, state, goal, reviewTick, identity, resource, row.Target, stock, started, selection);
                    if (dispatched)
                    {
                        return result0;
                    }

                    break;
                }
            }
        }

        if (supply.Acquisitions(resource).Count > 0 || supply.Plan.OpenedIds(resource, CandidateKind.Trade).Count > 0)
        {
            return new RoundsResourceResult { Verdict = BuildingRuntimePackage.ClaimHeld(resource.Value) };
        }

        if (row.Busy)
        {
            return new RoundsResourceResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks };
        }

        var tail = row.Sel;
        tail.Selected = [];
        var (result, _) = AcquireFromSources(call, epoch, state, goal, reviewTick, identity, resource, row.Target, stock, started, tail);
        return result;
    }

    internal (RoundsResourceResult Result, bool Dispatched) AcquireFromSources(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, Tick reviewTick, C.Identity identity, Resource resource, long target, Fact<IReadOnlyList<Amount>> stock, DateTimeOffset started, SourceSelection? pre)
    {
        if (pre is null)
        {
            var remote = MiningReach(call, state, reviewTick);
            var (sel, ok) = SourcesForDeficit(call, identity, resource, target, stock, remote);
            if (!ok)
            {
                return (new RoundsResourceResult { Verdict = NoResourceSource(resource) }, false);
            }

            pre = sel;
        }

        var selection = pre.Value;
        var selected = selection.Selected ?? [];
        var (zoneResult, handled) = MaterialStorageBlocked(selected, selection.Storage);
        if (handled)
        {
            return (zoneResult with { Sources = selected }, true);
        }

        var (result, dispatched) = DispatchMineSource(call, epoch, state, goal, resource, selected, started);
        if (!dispatched)
        {
            (result, dispatched) = TunnelToBuriedOre(call, epoch, state, goal, reviewTick, identity, resource);
        }

        if (dispatched)
        {
            return (result with { Sources = selected }, true);
        }

        if (selection.Designated)
        {
            return (new RoundsResourceResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks, Sources = selected }, false);
        }

        if (selection.None)
        {
            return (new RoundsResourceResult { Verdict = BuildingRuntimePackage.NoSource(resource) }, false);
        }

        return (new RoundsResourceResult { Verdict = NoResourceSource(resource), Sources = selected }, false);
    }

    internal (SourceSelection Selection, bool Ok) SourcesForDeficit(CancellationToken ct, C.Identity identity, Resource resource, long target, Fact<IReadOnlyList<Amount>> stock, RemoteWorkRequest remote)
    {
        if (!stock.TryGetValue(out var rows))
        {
            return (default, false);
        }

        long have = 0;
        foreach (var row in rows)
        {
            if (row.Resource == resource)
            {
                have = row.Count;
                break;
            }
        }

        if (have >= target)
        {
            return (default, true);
        }

        IReadOnlyList<ResourceSource> sources;
        ResourceStorage storage;
        try
        {
            (sources, storage, _) = native.ReadResourceSources(ct, identity, resource.Value);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (default, false);
        }

        var (selected, _) = PolicyFunctions.SelectReachableResourceSources(sources, target, have, remote);
        var output = new SourceSelection { Selected = selected, Storage = storage, None = sources.Count == 0 };
        foreach (var s in sources)
        {
            output.Designated = output.Designated || (s.Method == ResourceSourceMethod.Mine && s.Designated);
        }

        return (output, true);
    }

    internal RemoteWorkRequest MiningReach(CancellationToken ct, ControlState state, Tick tick)
    {
        var (last, _) = reviewer.Native.Identity(ct);
        var expected = ObservationPackage.DecodeIdentity(last);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, tick))
        {
            throw new ControlException("miningReach: boundary moved");
        }

        var reading = reviewer.ObserveOwned(ct, reviewer.Native, expected, Unknown<IReadOnlyList<ConstructionClaim>>());
        var f = reading.Projection.Facts;
        f = f with { ConstructionClaims = reviewer.Player.Journal.ConstructionClaims(ct, state.Snapshot, expected.Tick) };
        var emergency = PolicyFunctions.NewEmergencySnapshot(state.Snapshot, expected.Tick, reading.Emergency);
        f = f with { Hostiles = PolicyFunctions.EmergencyNeeds(emergency, state.Snapshot, expected.Tick).Hostiles };
        f = f with { UrgentPatients = PolicyFunctions.UrgentPatients(emergency, state.Snapshot, expected.Tick) };
        f = f with { UrgentPatients = PolicyFunctions.AmputationNeeds(emergency, f.MedicalPawns, Unknown<long>(), f.UrgentPatients).UrgentPatients };
        var extent = PolicyFunctions.DeriveColonyExtent(new ColonyExtentRequest { Bounds = f.MapBounds, Construction = f.CurrentConstruction, Claims = f.ConstructionClaims, Home = f.HomeCoverage });
        var request = new RemoteWorkRequest { Safety = PolicyFunctions.LootSafety(f), Extent = extent, Competition = PolicyFunctions.RemoteCompetition(f) };
        var (layout, ok) = reviewer.LayoutPlan(ct, state.Snapshot, expected.Tick);
        if (ok)
        {
            request = request with { Plan = Known(layout.Plan) };
        }

        return request;
    }

    internal static (RoundsResourceResult Result, bool Handled) MaterialStorageBlocked(IReadOnlyList<ResourceSource> selected, ResourceStorage storage)
    {
        var (_, _, blocked) = PolicyFunctions.SelectResourceStorageZone(selected, 0, storage);
        if (!blocked)
        {
            return (default, false);
        }

        return (new RoundsResourceResult { Verdict = BuildingRuntimePackage.NoSpace("material_storage") }, true);
    }

    internal (RoundsResourceResult Result, bool Dispatched) DispatchMineSource(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, Resource resource, IReadOnlyList<ResourceSource> sources, DateTimeOffset started)
    {
        foreach (var source in sources)
        {
            if (source.Method != ResourceSourceMethod.Mine)
            {
                continue;
            }

            var p = reviewer.Player;
            var acquisitionValue = Acquisition.NewAcquisition(source.ThingId, resource.Value, source.Cell);
            var baseKey = $"{goal.Standard.Id}/{goal.Standard.Episode}/mine/{source.ThingId}/{source.Cell.X}/{source.Cell.Z}";
            MethodId MineMethod(int attempt)
            {
                var key = attempt > 0 ? $"{baseKey}/{attempt}" : baseKey;
                var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
                return new MethodId("resource-mine-" + Convert.ToHexStringLower(digest.AsSpan(0, 16)));
            }

            var plansByMethod = new Dictionary<MethodId, PlanId>();
            foreach (var m in goal.History)
            {
                if (m.Episode == goal.Standard.Episode)
                {
                    plansByMethod[m.Method] = m.Plan;
                }
            }

            var earlier = new List<PlanId>();
            var attempt = 0;
            while (plansByMethod.TryGetValue(MineMethod(attempt), out var bound))
            {
                earlier.Add(bound);
                attempt++;
            }

            var (_, admitted) = BuildingRuntimePackage.AdmitSubject(call, p.Journal, baseKey, earlier, state.Snapshot);
            if (!admitted)
            {
                continue;
            }

            var id = PlanIds.MintPlanId();
            var methodId = MineMethod(attempt);
            var action = DomainAction.NewMineAcquisitionAction(new ActionId($"{id}-0"), acquisitionValue);
            var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
            p.Current(call, epoch);
            var elapsed = reviewer.Clock.Now() - started;
            if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
            {
                throw new ControlException("dispatchMineSource: session state changed or the read took too long");
            }

            try
            {
                p.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, methodId, plan);
            }
            catch (MethodBoundException)
            {
                continue;
            }

            return (new RoundsResourceResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id }, true);
        }

        return (default, false);
    }

    internal static IReadOnlyList<string> RecipeIngredientNames(IReadOnlyList<GearBenchRead> census, Resource product)
    {
        var ingredients = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in census)
        {
            if (!row.Bench.Recipes.TryGetValue(out var recipes))
            {
                continue;
            }

            foreach (var recipe in recipes)
            {
                if (product.Value != "" && !recipe.Products.Contains(product))
                {
                    continue;
                }

                if (!recipe.Ingredients.TryGetValue(out var slots))
                {
                    continue;
                }

                foreach (var slot in slots)
                {
                    foreach (var alt in slot)
                    {
                        ingredients.Add(alt.Resource.Value);
                    }
                }
            }
        }

        return [.. ingredients];
    }
}
