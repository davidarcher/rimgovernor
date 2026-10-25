using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

/// <summary>What the resource planner decided: a plan, a wait, or the sources it settled on.</summary>
public readonly record struct ResourceResult : IPlannerResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }

    public IReadOnlyList<ResourceSource> Sources { get; init; }

    /// <summary>True when the resource went nowhere that would consume its turn: claimed, used, held without a source, or awaiting a source.</summary>
    internal bool Undispatched()
    {
        var v = Verdict;
        return v.Is(Cause.MethodUsed) || v.Is(Cause.Claim) || v.Is(Cause.HeldNoSource) || (v.Is(Cause.AwaitingPlan) && v.Refusal.Subject == ResourcePlanner.ResourceSourceSubject);
    }
}

internal record struct SourceSelection
{
    public IReadOnlyList<ResourceSource> Selected { get; set; }

    public ResourceStorage Storage { get; set; }

    public bool Designated { get; set; }

    public bool None { get; set; }
}

/// <summary>The economy/resource Need's mining work: ranks the resource targets and mines, produces or tunnels toward the first one short.</summary>
public sealed partial class ResourcePlanner
{
    internal const string ResourceSourceSubject = "resource_source";

    private readonly ColonyReview reviewer;
    private readonly GameClient native;

    private ResourcePlanner(ColonyReview reviewer, GameClient native)
    {
        this.reviewer = reviewer;
        this.native = native;
    }

    public static ResourcePlanner NewReviewResourcePlanner(ColonyReview reviewer, GameClient native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewResourcePlanner: reviewer == null || native == null");
        }

        reviewer.ResourceNative = native;
        return new ResourcePlanner(reviewer, native);
    }

    internal static EquatableArray<Amount> ResourceStockFacts(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v)
    {
        var rows = new List<Amount>();
        foreach (var q in v.Resources)
        {
            rows.Add(new Amount(new Resource(q.DefName), q.Units));
        }

        return [.. rows];
    }

    internal static Verdict NoResourceSource(Resource resource) => Verdict.AwaitingPlan(ResourceSourceSubject, resource.Value);

    /// <summary>
    /// One step under <paramref name="owner"/>, the economy/resource Need its family's work step passes. Its mines,
    /// tunnels and deep drills are Methods of the Need, admitted by their claims beside the Need's other work.
    /// </summary>
    internal async Task<ResourceResult> StepAsync(StepArbiter arbiter, NeedOwner owner, CancellationToken call, CancellationToken epoch)
    {
        if (owner is not NeedOwner { Need: var need } || need != EconomyFamily.Resource)
        {
            throw new ControlException("resource step: the step runs only under the economy/resource Need");
        }
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new ResourceResult { Verdict = Verdict.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var targets = reviewer.ResourceTargets(state.Snapshot);
        if (targets.Count == 0)
        {
            return new ResourceResult { Verdict = Verdict.BuildingReasonDisabled };
        }

        var review = p.Journal.LoadReview(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new ResourceResult { Verdict = Verdict.BuildingReasonNoReview };
        }

        using var recorded = PlannerSnapshot.RecordPlannerStep(EconomyFamily.Resource.FilingKey, state.Snapshot, review.Tick);
        try
        {
            return await StepRecordedAsync(state, review, owner, call, epoch).ConfigureAwait(false);
        }
        finally
        {
            recorded.Finish();
        }
    }

    private async Task<ResourceResult> StepRecordedAsync(ControlState state, ReviewStep review, NeedOwner goal, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        // A Need-owned step has no whole-owner guard: each mine, tunnel and drill is admitted by its own claims, and
        // a MethodId already committed is not re-issued.
        BuildingPlanner.CancelStalledExcavation(p.Journal, goal, review.Tick, call);
        var started = reviewer.Clock.Now();
        var identity = Boundary.Identity(state.Snapshot);
        var observed = await native.ReadColonyFactsAsync(identity, call).ConfigureAwait(false);
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
        var ranked = ResourceRules.RankResourceTargets(ResourceRules.ResourceConcernTargets(targets, supply.Derived), stock);
        var have = StockReader.Of(stock);
        ResourceResult? first = null;
        var sourceless = new List<Resource>();
        foreach (var row in ranked)
        {
            ResourceResult result;
            if (row.Resource.Value == "Beer")
            {
                result = await DispatchResourceConcernAsync(state, goal, review.Tick, identity, row.Resource, row.Target, stock, started, call, epoch).ConfigureAwait(false);
            }
            else if (have.Count(row.Resource) >= row.Target)
            {
                continue;
            }
            else
            {
                result = await DispatchSuppliedAsync(state, goal, review.Tick, identity, supply, row.Resource, stock, started, call, epoch).ConfigureAwait(false);
            }

            if (!result.Undispatched() || result.Plan != default)
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
            return new ResourceResult { Verdict = Verdict.BuildingReasonNoDeficit };
        }

        var chosen = first.Value;
        if (sourceless.Count > 0)
        {
            chosen = chosen with { Verdict = Verdict.NoSource([.. sourceless]) };
        }

        return chosen;
    }

    internal async Task<(ResourceMethod Choice, IReadOnlyList<Stock> Supply)> MethodChoiceAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, Resource resource, long target, CancellationToken call)
    {
        var census = await native.ReadGearBenchesAsync(identity, call).ConfigureAwait(false);
        var benches = census.Select(row => row.Bench).ToList();
        IReadOnlyList<Stock> supply = [];
        var names = RecipeIngredientNames(census, resource);
        if (names.Count > 0)
        {
            supply = await native.ReadSupplyStockAsync(identity, names, call).ConfigureAwait(false);
        }

        var request = new ResourceMethodRequest { Resource = resource, Target = target, Benches = [.. benches] };
        SnapshotPlannerReplay.NoteResourceMethod(request);
        return (ResourceRules.SelectResourceMethod(request), supply);
    }

    internal async Task<ResourceResult> DispatchResourceConcernAsync(ControlState state, NeedOwner goal, Tick reviewTick, RimGovernor.Brain.GameFacts.Common.Identity identity, Resource resource, long target, EquatableArray<Amount> stock, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
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
                return new ResourceResult { Verdict = Verdict.BuildingReasonExistingWork };
        }

        var (result, _) = await AcquireFromSourcesAsync(state, goal, reviewTick, identity, resource, target, stock, started, null, call, epoch).ConfigureAwait(false);
        return result;
    }

    internal async Task<ResourceResult> DispatchSuppliedAsync(ControlState state, NeedOwner goal, Tick reviewTick, RimGovernor.Brain.GameFacts.Common.Identity identity, ResourceSupplyState supply, Resource resource, EquatableArray<Amount> stock, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        if (!supply.Rows.TryGetValue(resource, out var row) || row is null)
        {
            return new ResourceResult { Verdict = NoResourceSource(resource) };
        }

        if (!row.SelKnown && row.Choice.Kind != ResourceMethodKind.Produce)
        {
            return new ResourceResult { Verdict = NoResourceSource(resource) };
        }

        var mines = supply.Plan.OpenedIDs(resource, CandidateKind.Mining);
        foreach (var e in supply.Plan.Opened(resource))
        {
            switch (e.Candidate.Kind)
            {
                case CandidateKind.Produce:
                    return new ResourceResult { Verdict = Verdict.BuildingReasonExistingWork };
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
            return new ResourceResult { Verdict = Verdict.ClaimHeld(resource.Value) };
        }

        if (row.Busy)
        {
            return new ResourceResult { Verdict = Verdict.BuildingReasonExistingWork };
        }

        var tail = row.Sel;
        tail.Selected = [];
        var (result, _) = await AcquireFromSourcesAsync(state, goal, reviewTick, identity, resource, row.Target, stock, started, tail, call, epoch).ConfigureAwait(false);
        return result;
    }

    internal async Task<(ResourceResult Result, bool Dispatched)> AcquireFromSourcesAsync(ControlState state, NeedOwner goal, Tick reviewTick, RimGovernor.Brain.GameFacts.Common.Identity identity, Resource resource, long target, EquatableArray<Amount> stock, DateTimeOffset started, SourceSelection? pre, CancellationToken call, CancellationToken epoch)
    {
        if (pre is null)
        {
            var remote = await MiningReachAsync(state, reviewTick, call).ConfigureAwait(false);
            var (sel, ok) = await SourcesForDeficitAsync(identity, resource, target, stock, remote, call).ConfigureAwait(false);
            if (!ok)
            {
                return (new ResourceResult { Verdict = NoResourceSource(resource) }, false);
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
            return (new ResourceResult { Verdict = Verdict.BuildingReasonExistingWork, Sources = selected }, false);
        }

        if (selection.None)
        {
            return (new ResourceResult { Verdict = Verdict.NoSource(resource) }, false);
        }

        return (new ResourceResult { Verdict = NoResourceSource(resource), Sources = selected }, false);
    }

    internal async Task<(SourceSelection Selection, bool Ok)> SourcesForDeficitAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, Resource resource, long target, EquatableArray<Amount> stock, RemoteWorkRequest remote, CancellationToken ct)
    {
        long have = 0;
        foreach (var row in stock)
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
            (sources, storage) = await native.ReadResourceSourcesAsync(identity, resource.Value, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (default, false);
        }

        var selected = ResourceRules.SelectReachableResourceSources(sources, target, have, remote);
        var output = new SourceSelection { Selected = selected, Storage = storage, None = sources.Count == 0 };
        foreach (var s in sources)
        {
            output.Designated = output.Designated || (s.Method == ResourceSourceMethod.Mine && s.Designated);
        }

        return (output, true);
    }

    internal async Task<RemoteWorkRequest> MiningReachAsync(ControlState state, Tick tick, CancellationToken ct)
    {
        var last = await reviewer.Native.IdentityAsync(ct).ConfigureAwait(false);
        var expected = new ObservationIdentity { Context = last.Context, Paused = last.Paused };
        if (!BuildingPlanner.BuildingBoundary(expected, state.Snapshot, tick))
        {
            throw new ControlException("miningReach: boundary moved");
        }

        var reading = await reviewer.ObserveOwnedAsync(reviewer.Native, expected, ct).ConfigureAwait(false);
        var f = reading.Reading.Facts;
        f = f with { ConstructionClaims = reviewer.Player.Journal.ConstructionClaims(state.Snapshot, expected.Tick, ct) };
        var emergency = EmergencySnapshot.New(state.Snapshot, expected.Tick, reading.Emergency);
        f = f with { Hostiles = EmergencyNeedsRules.EmergencyNeeds(emergency, state.Snapshot, expected.Tick).Hostiles };
        f = f with { UrgentPatients = EmergencyNeedsRules.UrgentPatients(emergency, state.Snapshot, expected.Tick) };
        f = f with { UrgentPatients = EmergencyNeedsRules.PatientCounts(reading.Emergency, f.MedicalPawns).Urgent };
        var extent = ColonyExtentRules.DeriveColonyExtent(f.MapBounds, f.CurrentConstruction, f.ConstructionClaims, f.HomeCoverage);
        var request = new RemoteWorkRequest { Safety = RemoteRules.LootSafety(f), Extent = extent, Competition = RemoteRules.RemoteCompetition(f) };
        var (layout, ok) = await reviewer.LayoutPlanAsync(state.Snapshot, expected.Tick, ct).ConfigureAwait(false);
        if (ok)
        {
            request = request with { Plan = layout.Plan };
        }

        return request;
    }

    internal static (ResourceResult Result, bool Handled) MaterialStorageBlocked(IReadOnlyList<ResourceSource> selected, ResourceStorage storage)
    {
        var blocked = ResourceRules.ResourceStorageBlocked(selected, storage);
        if (!blocked)
        {
            return (default, false);
        }

        return (new ResourceResult { Verdict = Verdict.NoSpace("material_storage") }, true);
    }

    internal (ResourceResult Result, bool Dispatched) DispatchMineSource(ControlState state, NeedOwner goal, Resource resource, IReadOnlyList<ResourceSource> sources, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        foreach (var source in sources)
        {
            if (source.Method != ResourceSourceMethod.Mine)
            {
                continue;
            }

            var p = reviewer.Player;
            var acquisitionValue = new Acquisition(source.ThingId, resource.Value, source.Cell);
            // The MethodId names the mined source: the Need's history in its world, not a Standard episode, keys it.
            var baseKey = $"{goal.OwnerKey}/mine/{source.ThingId}/{source.Cell.X}/{source.Cell.Z}";
            MethodId MineMethod(int attempt)
            {
                var key = attempt > 0 ? $"{baseKey}/{attempt}" : baseKey;
                var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
                return new MethodId("resource-mine-" + Convert.ToHexStringLower(digest.AsSpan(0, 16)));
            }

            var plansByMethod = new Dictionary<MethodId, PlanId>();
            foreach (var m in p.Journal.OwnerMethodHistory(goal, call))
            {
                plansByMethod[m.Method] = m.Plan;
            }

            var earlier = new List<PlanId>();
            var attempt = 0;
            while (plansByMethod.TryGetValue(MineMethod(attempt), out var bound))
            {
                earlier.Add(bound);
                attempt++;
            }

            if (earlier.Count > 0 && StorePackage.PlanOpen(p.Journal.LoadPlan(earlier[^1], call)))
            {
                // This source's mine is still at work; the other sources go on beside it.
                continue;
            }

            var (_, admitted) = ReviewRefusalBudget.AdmitSubject(p.Journal, baseKey, earlier, state.Snapshot, call);
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

            WorkCommit commit;
            try
            {
                commit = p.Journal.CommitOwnerWork(goal, methodId, "mine " + resource.Value, plan, call);
            }
            catch (MethodBoundException)
            {
                continue;
            }

            if (commit.Outcome == Sto.ClaimOutcome.Conflict)
            {
                // Another Method holds the source: the next source is tried.
                continue;
            }
            if (commit.Outcome == Sto.ClaimOutcome.Refused)
            {
                // The shared admission turned the Method down: nothing was written, so no plan is named.
                return (new ResourceResult { Verdict = BuildingPlanner.OwnerCommitVerdict(commit) }, true);
            }

            return (new ResourceResult { Verdict = BuildingPlanner.OwnerCommitVerdict(commit), Plan = id }, true);
        }

        return (default, false);
    }

    internal static IReadOnlyList<string> RecipeIngredientNames(IReadOnlyList<GearBenchRead> census, Resource product)
    {
        var ingredients = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in census)
        {
            foreach (var recipe in row.Bench.Recipes)
            {
                if (product.Value != "" && !recipe.Products.Contains(product))
                {
                    continue;
                }

                foreach (var slot in recipe.Ingredients)
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
