using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

/// <summary>One product the workshop considers, with the recipe hosts that make it.</summary>
internal sealed record WorkshopProduct(Pol.Resource Resource, IReadOnlyList<Pol.RecipeHost> Hosts);

/// <summary>What the pre-observation reads settled for one step: the deficit products and the bench definitions the census must describe.</summary>
internal sealed class WorkshopSelection
{
    public bool Barrel { get; init; }
    public List<WorkshopProduct> Products { get; } = [];
    public Pol.Resource Resource { get; init; }
    public List<string> Candidates { get; init; } = [];
}

public sealed partial class BuildingPlanner
{
    /// <summary>The workshop for replacement gear, owned by the military/equipment Need (#3259).</summary>
    public static BuildingPlanner NewReviewGearWorkshopPlanner(ColonyReview? reviewer, IReviewBuildingSource? native)
    {
        var planner = NewReviewWorkshopPlanner(reviewer, native);
        planner.Concern = MilitaryFamily.Equipment.FilingKey;
        planner.Need = MilitaryFamily.Equipment;
        return planner;
    }

    /// <summary>Whether this workshop is the equipment Need's, for replacement gear, rather than a resource target's.</summary>
    internal bool GearLadder() => Concern == MilitaryFamily.Equipment.FilingKey;

    /// <summary>
    /// Hosts a production load for a resource deficit, a Method of the economy/resource Need admitted by its claims (#3303);
    /// replacement gear takes the same path under the military/equipment Need. A product a usable standing bench offers is
    /// that bench's (its bills run there). Otherwise the deficit is an unhosted ledger order whose host the shared layout
    /// comparison chooses among reuse, each catalog bench and deferral, priced by the canonical ledger (RG-HABITAT-005):
    /// this planner binds the held plan's host (<see cref="PlannedHostStepAsync"/>) and never selects a bench itself. It
    /// records the research still gating a product no definition can host yet (the review turns that into the
    /// economy/research Need's target). Bills on the bench belong to the resource or gear planner.
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
    /// Reads the deficit resource, the current bench census and the recipe catalog before the planning census is requested,
    /// so the census can describe the definitions that host a recipe for it. A usable standing bench offering an available
    /// recipe ends the step (ExistingFacility): the declarer names its kind and its bills run there. A non-zero verdict ends
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
        var selection = new WorkshopSelection { Resource = resource };
        var gated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var product in products)
        {
            if (WorkRules.StandingWorkshopRecipe(product, benches) is { } recipe)
            {
                RecordWorkshopLadder(state, review, product, null, recipe, call);
                return (null, BuildingReasonExistingFacility);
            }
            var hosts = defs.Catalog.RecipeHosts(product.ToString(), finished);
            var candidatesFor = WorkRules.WorkshopResearchCandidates(product, hosts);
            if (candidatesFor.Count == 0)
            {
                continue;
            }
            selection.Products.Add(new WorkshopProduct(product, hosts));
            gated.UnionWith(candidatesFor);
        }
        if (selection.Products.Count == 0)
        {
            return (null, BuildingReasonWorkshopUnavailable);
        }
        selection.Candidates.AddRange(gated.Order(StringComparer.Ordinal));
        return (selection, new Verdict());
    }

    /// <summary>
    /// Persists the rung the workshop settled on so the next rounds can raise the economy/research Need for a research-gated
    /// host (<paramref name="rung"/>), or clear that need once a host is buildable or a standing bench offers
    /// <paramref name="recipe"/>.
    /// </summary>
    internal void RecordWorkshopLadder(ControlState state, Sto.ReviewState review, Pol.Resource resource, Pol.WorkshopRung? rung, string recipe, CancellationToken call)
    {
        var record = new Sto.ProductionLadderRecord
        {
            World = new Sto.World(state.Snapshot.Colony, state.Snapshot.Load, state.Snapshot.Map),
            Tick = review.Tick,
            Resource = resource,
            Bench = rung?.Bench ?? "",
            Recipe = rung?.Recipe ?? recipe,
            Concern = Concern,
        };
        if (rung is not null)
        {
            record = record with { Research = rung.Research };
        }
        Reviewer.Player.Journal.SaveProductionLadder(record, call);
    }

    /// <summary>
    /// The AwaitingPlan subject of an owner's load the comparison can host while the held plan binds none of it: no layout
    /// plan is held yet, or the ledger holds no unhosted order of it yet. No bench is chosen in its place.
    /// </summary>
    internal const string HostUnplanned = "host_unplanned";

    /// <summary>
    /// The workshop's step once the held plan binds none of its loads (<see cref="PlannedHostStepAsync"/>): a fermenting barrel
    /// prerequisite is placed as before; otherwise no bench is selected here. A product a buildable definition can host
    /// waits on the shared layout comparison (<see cref="HostUnplanned"/>), which compares reuse, each catalog host and
    /// deferral; a product only research stands between records that rung (WorkshopResearch); else no host exists.
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
        var definitions = facts.Facts.Definitions.Select(d => new Pol.BenchDefinition { Name = d.Name, Available = d.Available, Research = d.Research }).ToArray();
        // A hostable product precedes a research-gated alternative: a missing layer's armor suggestion cannot hide a buildable shirt.
        if (Workshop.Products.FirstOrDefault(p => WorkRules.WorkshopHostable(p.Resource, p.Hosts, definitions)) is { } hostable)
        {
            RecordWorkshopLadder(state, review, hostable.Resource, null, "", call);
            return (null, AwaitingPlan(HostUnplanned, hostable.Resource.ToString()));
        }
        foreach (var product in Workshop.Products)
        {
            if (WorkRules.WorkshopResearchRung(product.Resource, product.Hosts, definitions) is { } rung)
            {
                RecordWorkshopLadder(state, review, product.Resource, rung, "", call);
                return (null, BuildingReasonWorkshopResearch);
            }
        }
        RecordWorkshopLadder(state, review, Workshop.Resource, null, "", call);
        return (null, BuildingReasonWorkshopUnavailable);
    }
}

public sealed partial class BuildingPlanner
{
    /// <summary>The AwaitingPlan subject of an owner's unhosted load the held plan hosts nowhere (HostBinding.Deferred).</summary>
    internal const string HostDeferred = "host_deferred";

    /// <summary>The AwaitingPlan subject of a spare's removal waiting on a bill a pawn is working (HostRetirement.Held).</summary>
    internal const string HostRetireHeld = "host_retire_held";

    /// <summary>The retire Method prefix of a spare host its serving replacement retires.</summary>
    internal const string HostRetirePrefix = "host-retire";

    /// <summary>
    /// The step for the loads the held plan hosts (RG-HABITAT-005, RG-HABITAT-011): the recipes this Need declared as
    /// ledger orders the plan hosts (<see cref="PlannedRules.OwnedHostedRecipes"/>: unhosted, or at a kind the plan hosts
    /// them away from) are bound to the plan's host (<see cref="PlannedHostBindings"/>, <see cref="BindPlannedHostAsync"/>),
    /// never re-selected. A spare the plan's serving replacement retires is removed first, and a removal a pawn's work in
    /// progress holds waits naming the bills (<see cref="PlannedRules.HostRetirementsOf"/>). A workshop binds the recipes of
    /// its prepared products (the replacement and its alternatives); the cooking and butcher Needs bind every load they own
    /// (a meal, a butchering). Null when there is no layout plan, the workshop places a barrel, or this Need owns none of
    /// them: the caller's selection waits on the comparison (<see cref="HostUnplanned"/>).
    /// </summary>
    internal async Task<BuildingResult?> PlannedHostStepAsync(ControlState state, Sto.ReviewState review, Sto.NeedOwner goal, RimGovernor.Brain.GameAccess.ColonyReading reading, CancellationToken call, CancellationToken epoch)
    {
        if (Workshop is { Barrel: true } || reading.Facts.LayoutPlan is not { } plan)
        {
            return null;
        }
        var view = Reviewer.WorkLedgerView();
        IReadOnlyList<string> owned = PlannedRules.OwnedHostedRecipes(view, Concern, plan);
        if (Workshop is { } workshop)
        {
            owned = workshop.Products.SelectMany(p => p.Hosts).Select(h => h.Definition).Where(owned.Contains).Distinct().ToArray();
        }
        if (owned.Count == 0)
        {
            return null;
        }
        foreach (var retirement in PlannedRules.HostRetirementsOf(plan, view, owned, PlannedRules.ClaimedBy(reading.Facts, Concern)))
        {
            if (retirement is HostRetirement.Held held)
            {
                return new BuildingResult { Verdict = AwaitingPlan(HostRetireHeld, held.Fact) };
            }
            var retire = (HostRetirement.Retire)retirement;
            var building = reading.Facts.CurrentConstruction.Buildings.FirstOrDefault(b => b.Id == retire.Bench && b.Cells.Count > 0)
                ?? throw new RequiredFactException("host retire building:" + retire.Bench);
            var retired = await RetireBuildingAsync(state, review, goal, reading, retire.Bench, retire.Def, building.Cells[0], HostRetirePrefix, "host_retire_method", call, epoch).ConfigureAwait(false);
            if (!retired.Verdict.Is(Pol.Cause.MethodUsed))
            {
                return retired;
            }
        }
        return await BindPlannedHostAsync(state, review, goal, reading, PlannedHostBindings(review, reading, owned), call, epoch).ConfigureAwait(false);
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
            var rr = new RoomReconcile { RingOnly = true, Room = build.Room, Name = PlannedRoomMethod(build.Room).Value, Reason = "planned host " + build.Recipe };
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
