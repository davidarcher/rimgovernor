namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsResourceSource : IColonySource, IFieldNative
{
    Task<(IReadOnlyList<GearBenchRead> Benches, BridgeResult Result)> ReadGearBenchesAsync(C.Identity identity, CancellationToken ct);

    Task<(IReadOnlyList<Stock> Stock, BridgeResult Result)> ReadSupplyStockAsync(C.Identity identity, IReadOnlyList<string> names, CancellationToken ct);

    Task<(IReadOnlyList<ResourceSource> Sources, ResourceStorage Storage, BridgeResult Result)> ReadResourceSourcesAsync(C.Identity identity, string resource, CancellationToken ct);
}

/// <summary>What the resource planner decided: a plan, a wait, or the sources it settled on.</summary>
public readonly record struct RoundsResourceResult : IPlannerMethod
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

    internal static Fact<EquatableArray<Amount>> ResourceStockFacts(O.ColonyFactsSnapshot v)
    {
        if (BuildingRuntimePackage.MedicalIssue(v.Issues, "resources"))
        {
            return Unknown<EquatableArray<Amount>>();
        }

        var rows = new List<Amount>();
        foreach (var q in v.Resources)
        {
            if (q.Units is null)
            {
                return Unknown<EquatableArray<Amount>>();
            }

            rows.Add(new Amount(new Resource((q.DefName ?? "")), (long)q.Units.GetValueOrDefault()));
        }

        return Known<EquatableArray<Amount>>([.. rows]);
    }

    internal static Verdict NoResourceSource(Resource resource) => Verdict.AwaitingPlan(ResourceSourceSubject, resource.Value);

    internal async Task<RoundsResourceResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
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

        using var recorded = PlannerSnapshot.RecordPlannerStep(PolicyFunctions.MaintainResource, state.Snapshot, review.Tick);
        try
        {
            return await StepRecordedAsync(state, review, call, epoch).ConfigureAwait(false);
        }
        finally
        {
            recorded.Finish();
        }
    }

    private async Task<RoundsResourceResult> StepRecordedAsync(ControlState state, RoundsReview review, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var (goal, workable) = p.Journal.Workable(review, PolicyFunctions.MaintainResource, call);
        if (!workable || goal is null)
        {
            return new RoundsResourceResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        BuildingRuntimePackage.CancelStalledExcavation(p.Journal, goal, review.Tick, call);
        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
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
        var (reply, _) = await native.ReadColonyFactsAsync(identity, false, call).ConfigureAwait(false);
        var observed = reply.Outcome?.Observed ?? throw new ControlException("step: observed == null");
        try
        {
            BridgeApi.ValidateColonyFacts(observed, identity);
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

        if ((observed.Context?.Tick).GetValueOrDefault() < review.Tick.Value)
        {
            throw new ControlException("step: colony facts older than the review");
        }

        var stock = ResourceStockFacts(observed);
        var (drillResult, drillHandled) = await DeepDrillAsync(state, goal, review, started, call, epoch).ConfigureAwait(false);
        if (drillHandled)
        {
            return drillResult;
        }

        var targets = reviewer.ResourceTargets(state.Snapshot);
        var supply = await reviewer.ResourceSupplyAsync(state, review, goal, call).ConfigureAwait(false);
        var ranked = PolicyFunctions.RankResourceTargets(PolicyFunctions.ResourceConcernTargets(targets, supply.Derived), stock);
        var have = new StockReader(stock, Unknown<long>());
        RoundsResourceResult? first = null;
        var sourceless = new List<Resource>();
        foreach (var row in ranked)
        {
            RoundsResourceResult result;
            if (row.Resource.Value == "Beer")
            {
                result = await DispatchResourceConcernAsync(state, goal, review.Tick, identity, row.Resource, row.Target, stock, started, call, epoch).ConfigureAwait(false);
            }
            else if (have.Units(row.Resource) >= row.Target)
            {
                continue;
            }
            else
            {
                result = await DispatchSuppliedAsync(state, goal, review.Tick, identity, supply, row.Resource, stock, started, call, epoch).ConfigureAwait(false);
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
            chosen = chosen with { Verdict = Verdict.NoSource([.. sourceless]) };
        }

        return chosen;
    }

    internal async Task<(ResourceMethod Choice, IReadOnlyList<Stock> Supply)> MethodChoiceAsync(C.Identity identity, Resource resource, long target, CancellationToken call)
    {
        var (census, _) = await native.ReadGearBenchesAsync(identity, call).ConfigureAwait(false);
        var benches = census.Select(row => row.Bench).ToList();
        IReadOnlyList<Stock> supply = [];
        var names = RecipeIngredientNames(census, resource);
        if (names.Count > 0)
        {
            (supply, _) = await native.ReadSupplyStockAsync(identity, names, call).ConfigureAwait(false);
        }

        var request = new ResourceMethodRequest { Resource = resource, Target = target, Benches = Known<EquatableArray<GearBench>>([.. benches]), Stock = [.. supply] };
        SnapshotPackage.NoteResourceMethod(request);
        return (PolicyFunctions.SelectResourceMethod(request), supply);
    }

    internal async Task<RoundsResourceResult> DispatchResourceConcernAsync(ControlState state, StandardState goal, Tick reviewTick, C.Identity identity, Resource resource, long target, Fact<EquatableArray<Amount>> stock, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        if (resource.Value == "Beer")
        {
            var items = await reviewer.ItemFactsAsync(state.Snapshot, call).ConfigureAwait(false);
            if (items.Wort.Value == "")
            {
                throw new ControlException("dispatchResourceConcern: the catalog names no wort def");
            }

            resource = items.Wort;
        }

        var (choice, _) = await MethodChoiceAsync(identity, resource, target, call).ConfigureAwait(false);
        switch (choice.Kind)
        {
            case ResourceMethodKind.Produce:
            case ResourceMethodKind.Wait:
                return new RoundsResourceResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks };
        }

        var (result, _) = await AcquireFromSourcesAsync(state, goal, reviewTick, identity, resource, target, stock, started, null, call, epoch).ConfigureAwait(false);
        return result;
    }

    internal async Task<RoundsResourceResult> DispatchSuppliedAsync(ControlState state, StandardState goal, Tick reviewTick, C.Identity identity, ResourceSupplyState supply, Resource resource, Fact<EquatableArray<Amount>> stock, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        if (!supply.Rows.TryGetValue(resource, out var row) || row is null)
        {
            return new RoundsResourceResult { Verdict = NoResourceSource(resource) };
        }

        if (!row.SelKnown && row.Choice.Kind != ResourceMethodKind.Produce)
        {
            return new RoundsResourceResult { Verdict = NoResourceSource(resource) };
        }

        var mines = supply.Plan.OpenedIDs(resource, CandidateKind.Mining);
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
                    var (result0, dispatched) = await AcquireFromSourcesAsync(state, goal, reviewTick, identity, resource, row.Target, stock, started, selection, call, epoch).ConfigureAwait(false);
                    if (dispatched)
                    {
                        return result0;
                    }

                    break;
                }
            }
        }

        if (supply.Acquisitions(resource).Count > 0 || supply.Plan.OpenedIDs(resource, CandidateKind.Trade).Count > 0)
        {
            return new RoundsResourceResult { Verdict = Verdict.ClaimHeld(resource.Value) };
        }

        if (row.Busy)
        {
            return new RoundsResourceResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks };
        }

        var tail = row.Sel;
        tail.Selected = [];
        var (result, _) = await AcquireFromSourcesAsync(state, goal, reviewTick, identity, resource, row.Target, stock, started, tail, call, epoch).ConfigureAwait(false);
        return result;
    }

    internal async Task<(RoundsResourceResult Result, bool Dispatched)> AcquireFromSourcesAsync(ControlState state, StandardState goal, Tick reviewTick, C.Identity identity, Resource resource, long target, Fact<EquatableArray<Amount>> stock, DateTimeOffset started, SourceSelection? pre, CancellationToken call, CancellationToken epoch)
    {
        if (pre is null)
        {
            var remote = await MiningReachAsync(state, reviewTick, call).ConfigureAwait(false);
            var (sel, ok) = await SourcesForDeficitAsync(identity, resource, target, stock, remote, call).ConfigureAwait(false);
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

        var (result, dispatched) = DispatchMineSource(state, goal, resource, selected, started, call, epoch);
        if (!dispatched)
        {
            (result, dispatched) = await TunnelToBuriedOreAsync(state, goal, reviewTick, identity, resource, call, epoch).ConfigureAwait(false);
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
            return (new RoundsResourceResult { Verdict = Verdict.NoSource(resource) }, false);
        }

        return (new RoundsResourceResult { Verdict = NoResourceSource(resource), Sources = selected }, false);
    }

    internal async Task<(SourceSelection Selection, bool Ok)> SourcesForDeficitAsync(C.Identity identity, Resource resource, long target, Fact<EquatableArray<Amount>> stock, RemoteWorkRequest remote, CancellationToken ct)
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
            (sources, storage, _) = await native.ReadResourceSourcesAsync(identity, resource.Value, ct).ConfigureAwait(false);
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

    internal async Task<RemoteWorkRequest> MiningReachAsync(ControlState state, Tick tick, CancellationToken ct)
    {
        var (last, _) = await reviewer.Native.IdentityAsync(ct).ConfigureAwait(false);
        var expected = ObservationPackage.DecodeIdentity(last);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, tick))
        {
            throw new ControlException("miningReach: boundary moved");
        }

        var reading = await reviewer.ObserveOwnedAsync(reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), ct).ConfigureAwait(false);
        var f = reading.Projection.Facts;
        f = f with { ConstructionClaims = reviewer.Player.Journal.ConstructionClaims(state.Snapshot, expected.Tick, ct) };
        var emergency = EmergencySnapshot.New(state.Snapshot, expected.Tick, reading.Emergency);
        f = f with { Hostiles = PolicyFunctions.EmergencyNeeds(emergency, state.Snapshot, expected.Tick).Hostiles };
        f = f with { UrgentPatients = PolicyFunctions.UrgentPatients(emergency, state.Snapshot, expected.Tick) };
        f = f with { UrgentPatients = PolicyFunctions.AmputationNeeds(emergency, f.MedicalPawns, Unknown<long>(), f.UrgentPatients).Urgent };
        var extent = PolicyFunctions.DeriveColonyExtent(new ColonyExtentRequest { Bounds = f.MapBounds, Construction = f.CurrentConstruction, Claims = f.ConstructionClaims, Home = f.HomeCoverage });
        var request = new RemoteWorkRequest { Safety = PolicyFunctions.LootSafety(f), Extent = extent, Competition = PolicyFunctions.RemoteCompetition(f) };
        var (layout, ok) = await reviewer.LayoutPlanAsync(state.Snapshot, expected.Tick, ct).ConfigureAwait(false);
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

        return (new RoundsResourceResult { Verdict = Verdict.NoSpace("material_storage") }, true);
    }

    internal (RoundsResourceResult Result, bool Dispatched) DispatchMineSource(ControlState state, StandardState goal, Resource resource, IReadOnlyList<ResourceSource> sources, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        foreach (var source in sources)
        {
            if (source.Method != ResourceSourceMethod.Mine)
            {
                continue;
            }

            var p = reviewer.Player;
            var acquisitionValue = new Acquisition(source.ThingId, resource.Value, source.Cell);
            var baseKey = $"{goal.Standard.Id}/{goal.Standard.Episode}/mine/{source.ThingId}/{source.Cell.X}/{source.Cell.Z}";
            MethodId MineMethod(int attempt)
            {
                var key = attempt > 0 ? $"{baseKey}/{attempt}" : baseKey;
                var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
                return new MethodId("resource-mine-" + Convert.ToHexStringLower(digest.AsSpan(0, 16)));
            }

            var plansByMethod = new Dictionary<MethodId, PlanId>();
            foreach (var m in goal.History)
            {
                if (m.Episode == goal.Standard.Episode)
                {
                    plansByMethod[m.MethodId] = m.Plan;
                }
            }

            var earlier = new List<PlanId>();
            var attempt = 0;
            while (plansByMethod.TryGetValue(MineMethod(attempt), out var bound))
            {
                earlier.Add(bound);
                attempt++;
            }

            var (_, admitted) = BuildingRuntimePackage.AdmitSubject(p.Journal, baseKey, earlier, state.Snapshot, call);
            if (!admitted)
            {
                continue;
            }

            var id = PlanIds.MintPlanId();
            var methodId = MineMethod(attempt);
            var action = DomainAction.NewMineAcquisitionAction(new ActionId($"{id}-0"), acquisitionValue);
            var plan = new PlanSpec(id, new PlanRevision(1), [action]);
            p.Current(call, epoch);
            var elapsed = reviewer.Clock.Now() - started;
            if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
            {
                throw new ControlException("dispatchMineSource: session state changed or the read took too long");
            }

            try
            {
                p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, methodId, plan, call);
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
