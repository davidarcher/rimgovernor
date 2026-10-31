using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

/// <summary>One product the workshop ladder considers, with the bench kinds that host a recipe for it.</summary>
internal sealed record WorkshopProduct(Pol.Resource Resource, IReadOnlyList<Pol.RecipeHost> Hosts);

/// <summary>What the pre-observation reads settled for one step: the deficit resource and the bench definitions the census must describe.</summary>
internal sealed class WorkshopSelection
{
    public bool Barrel { get; init; }
    public List<WorkshopProduct> Alternatives { get; } = [];
    public Pol.Resource Resource { get; set; }
    public IReadOnlyList<Pol.GearBench> Benches { get; init; } = [];
    public IReadOnlyList<Pol.RecipeHost> Hosts { get; set; } = [];
    public List<string> Candidates { get; set; } = [];

    /// <summary>The bench kinds whose standing benches no longer suffice (FurtherBenchKinds).</summary>
    public IReadOnlyList<string> Further { get; init; } = [];

    /// <summary>How many benches of the chosen definition already stand, which names a further bench's method.</summary>
    public int Ordinal { get; set; }

    public WorkshopSelection Copy()
    {
        var copy = (WorkshopSelection)MemberwiseClone();
        return copy;
    }
}

public sealed partial class BuildingPlanner
{
    /// <summary>The workshop ladder for replacement gear, owned by the military/equipment Need (#3259).</summary>
    public static BuildingPlanner NewReviewGearWorkshopPlanner(ColonyReview? reviewer, IReviewBuildingSource? native)
    {
        var planner = NewReviewWorkshopPlanner(reviewer, native);
        planner.Concern = MilitaryFamily.Equipment.FilingKey;
        planner.Need = MilitaryFamily.Equipment;
        return planner;
    }

    /// <summary>Whether this ladder is the equipment Need's, for replacement gear, rather than a resource target's.</summary>
    internal bool GearLadder() => Concern == MilitaryFamily.Equipment.FilingKey;

    /// <summary>
    /// Stages a production bench for a resource deficit, a Method of the economy/resource Need admitted by its
    /// claims (#3303); replacement gear walks the same ladder under the military/equipment Need. For a product no existing bench can produce: it discovers which player-buildable benches host a
    /// recipe for the resource, records the research still gating the first of them (the review turns that into
    /// the economy/research Need's target), furnishes a room whose native role can host a Workshop, and when no such room
    /// exists stages a starter shell first, the same ladder the rooms/comfort Need walks. A powered bench is staged when
    /// a generator definition is available; the power family (power/basic) then connects it as an unpowered consumer. Bills on
    /// the staged bench belong to the resource or gear planner.
    /// </summary>
    public static BuildingPlanner NewReviewWorkshopPlanner(ColonyReview? reviewer, IReviewBuildingSource? native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewWorkshopPlanner: reviewer == nil || native == nil");
        }
        if (native is not IReviewSource)
        {
            throw new ControlException("NewReviewWorkshopPlanner: !ok");
        }
        if (native is not GameClient)
        {
            throw new ControlException("NewReviewWorkshopPlanner: !ok");
        }
        return new BuildingPlanner { Reviewer = reviewer, Native = native, Concern = EconomyFamily.Resource.FilingKey, Need = EconomyFamily.Resource };
    }

    /// <summary>
    /// Reads the deficit resource, the current bench census and the recipe catalog before the planning census
    /// is requested, so the census can describe exactly the candidate bench definitions. A non-zero verdict ends
    /// the step.
    /// </summary>
    internal async Task<(WorkshopSelection? Selection, Verdict Verdict)> PrepareWorkshopAsync(ControlState state, Sto.ReviewState review, CancellationToken call)
    {
        if (!GearLadder())
        {
            var wanted = Reviewer.ResourceTargets(state.Snapshot);
            if (wanted.Count == 0)
            {
                return (null, BuildingReasonDisabled);
            }
        }
        if (Native is not GameClient source)
        {
            throw new ControlException("prepareWorkshop: !ok");
        }
        var identity = Boundary.Identity(state.Snapshot);
        var observed = await source.ReadColonyFactsAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("prepareWorkshop: err != nil || observed.Context.GetTick() < int64(review.Tick)");
        }
        if (observed.Context.Tick < review.Tick.Value)
        {
            throw new ControlException("prepareWorkshop: err != nil || observed.Context.GetTick() < int64(review.Tick)");
        }
        var defs = await Gear.GearDefinitionsOfAsync(Native, identity, call).ConfigureAwait(false);
        if (defs is null)
        {
            throw new ControlException("prepareWorkshop: the recipe hosts need the definition catalog and the finished research");
        }
        var finished = defs.Finished;
        Pol.Resource resource;
        List<Pol.Resource> products = [];
        if (GearLadder())
        {
            var tables = await source.FrameTablesAsync(identity, call).ConfigureAwait(false);
            var projection = ColonyPlanningReads.Compose(observed, new ObservationIdentity { Context = observed.Context }, tables, defs.Catalog);
            var facts = projection.Facts.Gear;
            foreach (var pawn in facts.Pawns)
            {
                if (pawn.Candidates.Count > 0)
                {
                    return (null, BuildingReasonExistingWork);
                }
            }
            var needs = GearRules.GearReplacementNeeds(facts);
            if (needs.Count == 0)
            {
                return (null, BuildingReasonNoDeficit);
            }
            resource = needs[0];
            products = [.. needs];
        }
        else
        {
            var stock = ResourcePlanner.ResourceStockFacts(observed);
            var wanted = Reviewer.ResourceTargets(state.Snapshot);
            var (picked, _, found) = ResourceRules.SelectResourceTarget(wanted, stock);
            if (!found)
            {
                return (null, BuildingReasonNoDeficit);
            }
            resource = picked;
        }
        var census = (await source.ReadGearBenchesAsync(identity, call).ConfigureAwait(false));
        var prerequisite = FacilityPlanningRules.WorkshopPrerequisiteOf(resource, observed.FermentingBarrels, nameof(RimWorld.ThingDefOf.Beer));
        if (prerequisite != WorkshopPrerequisite.Product)
        {
            if (prerequisite == WorkshopPrerequisite.Barrel)
            {
                var barrel = new WorkshopSelection { Barrel = true, Resource = resource, Candidates = ["FermentingBarrel"] };
                return (barrel, new Verdict());
            }
            var items = await Reviewer.ItemFactsAsync(state.Snapshot, call).ConfigureAwait(false);
            if (items.Wort.ToString() == "")
            {
                throw new ControlException("prepareWorkshop: the catalog names no wort def");
            }
            resource = items.Wort;
        }
        var benches = census.Select(row => row.Bench).ToList();
        if (products.Count == 0)
        {
            products = [resource];
        }
        var selection = new WorkshopSelection { Benches = benches, Further = Reviewer.LedgerFurtherBenches(benches) };
        var gated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var product in products)
        {
            var hosts = defs.Catalog.RecipeHosts(product.ToString(), finished);
            var request = new Pol.WorkshopRequest { Resource = product, Benches = [.. benches], Hosts = hosts, Further = selection.Further };
            var (existing, _) = WorkRules.ExistingWorkshopBench(request);
            if (existing is not null)
            {
                SnapshotPlannerReplay.NoteWorkshop(request);
                RecordWorkshopLadder(state, review, product, existing, call);
                return (null, BuildingReasonExistingFacility);
            }
            var candidatesFor = WorkRules.WorkshopResearchCandidates(product, hosts);
            if (candidatesFor.Count == 0)
            {
                continue;
            }
            if (selection.Resource.ToString() == "")
            {
                selection.Resource = product;
                selection.Hosts = hosts;
            }
            else
            {
                selection.Alternatives.Add(new WorkshopProduct(product, hosts));
            }
            foreach (var name in candidatesFor)
            {
                gated.Add(name);
            }
        }
        if (selection.Resource.ToString() == "")
        {
            return (null, BuildingReasonWorkshopUnavailable);
        }
        selection.Candidates = [.. gated.OrderBy(n => n, StringComparer.Ordinal)];
        selection.Candidates.AddRange(defs.Catalog.GeneratorDefinitions());
        return (selection, new Verdict());
    }

    /// <summary>
    /// Persists the rung the workshop settled on so the next rounds can raise the economy/research Need for a
    /// research-gated bench, or clear that need once the bench is buildable or standing.
    /// </summary>
    internal void RecordWorkshopLadder(ControlState state, Sto.ReviewState review, Pol.Resource resource, Pol.WorkshopChoice choice, CancellationToken call)
    {
        var record = new Sto.ProductionLadderRecord
        {
            World = new Sto.World(state.Snapshot.Colony, state.Snapshot.Load, state.Snapshot.Map),
            Tick = review.Tick,
            Resource = resource,
            Bench = choice.Definition,
            Recipe = choice.Recipe,
            Concern = Concern,
        };
        if (choice.Method == Pol.WorkshopMethod.Research)
        {
            record = record with { Research = choice.Research };
        }
        Reviewer.Player.Journal.SaveProductionLadder(record, call);
    }

    /// <summary>
    /// Resolves the prepared candidates against the planning census the same way SelectComfort resolves a
    /// comfort method: the chosen bench becomes the definition, placed indoors in a Workshop-hosting room.
    /// </summary>
    internal (BuildingPlanner? Planner, Verdict Verdict) SelectWorkshop(ControlState state, Sto.ReviewState review, RimGovernor.Brain.GameAccess.ColonyReading facts, CancellationToken call)
    {
        if (Workshop is null)
        {
            return (null, FieldUnavailable("workshop"));
        }
        if (Workshop.Barrel)
        {
            var barrelAvailable = ColonyPlanningRules.DefinitionAvailable(facts.Facts, "FermentingBarrel");
            if (!barrelAvailable)
            {
                return (null, BuildingReasonWorkshopUnavailable);
            }
            var barrel = Copy();
            barrel.Definition = "FermentingBarrel";
            barrel.Environment = Pol.PlacementEnvironment.Indoors;
            return (barrel, new Verdict());
        }
        var definitions = new List<Pol.BenchDefinition>(facts.Facts.Definitions.Count);
        var available = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var d in facts.Facts.Definitions)
        {
            definitions.Add(new Pol.BenchDefinition { Name = d.Name, Available = d.Available, NeedsPower = d.NeedsPower, ConstructionSkill = d.ConstructionSkill, Research = d.Research });
            available[d.Name] = d.Available;
        }
        // A viable replacement precedes an unavailable or research-gated alternative.
        // Otherwise a missing layer's armor suggestion can hide a buildable shirt.
        List<WorkshopProduct> products = [new WorkshopProduct(Workshop.Resource, Workshop.Hosts), .. Workshop.Alternatives];
        IEnumerable<WorkshopRequest> Requests()
        {
            foreach (var product in products)
            {
                var request = new WorkshopRequest
                {
                    Resource = product.Resource,
                    Benches = [.. Workshop.Benches],
                    Hosts = product.Hosts,
                    Definitions = definitions,
                    Power = FacilityPlanningRules.GeneratorAvailable(available, facts.Facts.GeneratorRows.Keys),
                    BuilderSkill = WorkRules.BuilderSkill(facts.Facts.ForecastObserved.WorkPawns),
                    Further = Workshop.Further,
                };
                SnapshotPlannerReplay.NoteWorkshop(request);
                yield return request;
            }
        }
        var (resource, choice) = WorkRules.SelectWorkshopProduct(Requests());
        RecordWorkshopLadder(state, review, resource, choice, call);
        switch (choice.Method)
        {
case Pol.WorkshopMethod.Existing:
                return (null, BuildingReasonExistingFacility);
            case Pol.WorkshopMethod.Research:
                return (null, BuildingReasonWorkshopResearch);
            case Pol.WorkshopMethod.Unavailable:
                return (null, BuildingReasonWorkshopUnavailable);
        }
        var resolved = Copy();
        var selected = Workshop.Copy();
        selected.Resource = resource;
        selected.Ordinal += WorkRules.WorkshopOrdinal(Workshop.Benches, choice.Definition);
        resolved.Workshop = selected;
        resolved.Definition = choice.Definition;
        resolved.Environment = Pol.PlacementEnvironment.Indoors;
        resolved.Stuff = ColonyPlanningRules.BuildStuff(facts.Facts, ResourceStockRules.Count(facts.Snapshot.Resources), resolved.Definition);
        return (resolved, new Verdict());
    }
}

public sealed partial class BuildingPlanner
{
    /// <summary>The AwaitingPlan subject of an owner's unhosted load the held plan hosts nowhere (HostBinding.Deferred).</summary>
    internal const string HostDeferred = "host_deferred";

    /// <summary>
    /// The workshop ladder's step for the loads the held plan hosts (RG-HABITAT-005, RG-HABITAT-011): the recipes of the
    /// prepared products (the replacement and its alternatives) this Need declared as unhosted ledger orders
    /// (<see cref="PlannedRules.OwnedUnhostedRecipes"/>) are bound to the plan's host (<see cref="PlannedHostBindings"/>,
    /// <see cref="BindPlannedHostAsync"/>), never re-selected. Null when there is no layout plan or this Need owns none of
    /// them: the ladder selects its bench as before.
    /// </summary>
    internal async Task<BuildingResult?> PlannedHostStepAsync(ControlState state, Sto.ReviewState review, Sto.NeedOwner goal, RimGovernor.Brain.GameAccess.ColonyReading reading, CancellationToken call, CancellationToken epoch)
    {
        if (Workshop is not { Barrel: false } workshop || reading.Facts.LayoutPlan is null)
        {
            return null;
        }
        var owned = PlannedRules.OwnedUnhostedRecipes(Reviewer.WorkLedgerView(), Concern);
        var recipes = workshop.Hosts.Concat(workshop.Alternatives.SelectMany(a => a.Hosts)).Select(h => h.Definition)
            .Where(owned.Contains).Distinct().ToArray();
        if (recipes.Length == 0)
        {
            return null;
        }
        return await BindPlannedHostAsync(state, review, goal, reading, PlannedHostBindings(review, reading, recipes), call, epoch).ConfigureAwait(false);
    }

    /// <summary>
    /// The plan's binding of each recipe (<see cref="PlannedRules.HostBindingOf"/>) against the review's in-memory room
    /// selection for its own snapshot (a record of another load, plan or authority reads empty) and the fresh census: a
    /// bench built, or a blueprint or frame of it, at its piece's anchor.
    /// </summary>
    internal IReadOnlyList<HostBinding> PlannedHostBindings(Sto.ReviewState review, RimGovernor.Brain.GameAccess.ColonyReading reading, IReadOnlyList<string> recipes)
    {
        var plan = reading.Facts.LayoutPlan ?? throw new ControlException("PlannedHostBindings: no layout plan");
        var record = Reviewer.RoomSelection.Get(review.Snapshot);
        var census = reading.Facts.CurrentConstruction;
        var built = census.Buildings.Where(b => b.Building is not null).Select(b => (b.Building!.Definition, b.Building.Cell)).ToHashSet();
        var sited = census.Sites.Where(s => s.Building is not null).Select(s => (s.Building!.Definition, s.Building.Cell)).ToHashSet();
        return [.. recipes.Select(recipe => PlannedRules.HostBindingOf(plan, recipe, record, room => PlannedRoomStartedOf(reading, plan, room), built, sited))];
    }

    /// <summary>
    /// Turns the bindings into work: the first Build opens its room's ring (a RingOnly reconcile, gated by the portfolio as
    /// every planned room is) and then commits exactly its piece (def, anchor, rotation, stuff) as a construction Method of
    /// this Need, admitted by its footprint claims (CommitBuilds, AdmitBuildingMethod: an overlapping Method is a
    /// Conflict). Otherwise nothing is bound: a Held load waits on the portfolio, a Standing one on its standing bench (built)
    /// or its open construction (sited), and a load hosted nowhere is deferred. None of these satisfies the Need.
    /// </summary>
    internal async Task<BuildingResult> BindPlannedHostAsync(ControlState state, Sto.ReviewState review, Sto.NeedOwner goal, RimGovernor.Brain.GameAccess.ColonyReading reading, IReadOnlyList<HostBinding> bindings, CancellationToken call, CancellationToken epoch)
    {
        var read = new ReviewReading { Reading = reading, Emergency = new EmergencyFacts(), Frame = new BridgeReviewFrame() };
        if (bindings.OfType<HostBinding.Build>().FirstOrDefault() is { } build)
        {
            var plan = reading.Facts.LayoutPlan ?? throw new ControlException("BindPlannedHostAsync: no layout plan");
            var rr = new RoomReconcile { RingOnly = true, Room = build.Room, Name = PlannedRoomMethod(build.Room).Value, Reason = "workshop host " + build.Recipe };
            var ring = await ReconcileRoomAsync(state, review, goal, read, null, rr, call, epoch).ConfigureAwait(false);
            if (!ring.Verdict.SkipsToPlacement)
            {
                return ring;
            }
            var work = new RoomWork { Rr = rr with { RingOnly = false }, Ops = [new Pol.Operation { Kind = Pol.OpKind.Build, Pieces = [build.Piece] }] };
            return await CommitBuildsAsync(state, review, goal, read, plan, work, call, epoch).ConfigureAwait(false);
        }
        if (bindings.OfType<HostBinding.Held>().FirstOrDefault() is { } held)
        {
            var assessed = Reviewer.RoomSelection.Get(review.Snapshot).Assessed.Contains(held.Room);
            return new BuildingResult { Verdict = AwaitingPlan(assessed ? PortfolioHeld : PortfolioUnassessed, held.Recipe) };
        }
        if (bindings.OfType<HostBinding.Standing>().FirstOrDefault() is { } standing)
        {
            return new BuildingResult { Verdict = standing.Built ? BuildingReasonExistingFacility : BuildingReasonExistingWork };
        }
        return new BuildingResult { Verdict = AwaitingPlan(HostDeferred, string.Join(",", bindings.Select(b => b.Recipe))) };
    }
}
