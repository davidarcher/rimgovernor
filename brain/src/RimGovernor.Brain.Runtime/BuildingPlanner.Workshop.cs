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
        if (resource.ToString() == "Beer")
        {
            if (observed.FermentingBarrels == 0)
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
            SnapshotPlannerReplay.NoteWorkshop(request);
            var choice = WorkRules.SelectWorkshopBench(request);
            if (choice.Method == Pol.WorkshopMethod.Existing)
            {
                RecordWorkshopLadder(state, review, product, choice, call);
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
        selection.Candidates.AddRange(PowerRules.GeneratorDefinitions);
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
        var choice = new Pol.WorkshopChoice { Method = Pol.WorkshopMethod.Unavailable };
        var resource = Workshop.Resource;
        foreach (var product in products)
        {
            var request = new Pol.WorkshopRequest
            {
                Resource = product.Resource,
                Benches = [.. Workshop.Benches],
                Hosts = product.Hosts,
                Definitions = definitions,
                Power = GeneratorAvailable(available),
                BuilderSkill = WorkRules.BuilderSkill(facts.Facts.ForecastObserved.WorkPawns),
                Further = Workshop.Further,
            };
            SnapshotPlannerReplay.NoteWorkshop(request);
            var candidate = WorkRules.SelectWorkshopBench(request);
            if (candidate.Method == Pol.WorkshopMethod.Build || candidate.Method == Pol.WorkshopMethod.Existing)
            {
                choice = candidate;
                resource = product.Resource;
                break;
            }
            if (candidate.Method == Pol.WorkshopMethod.Research || choice.Method == Pol.WorkshopMethod.Unavailable)
            {
                choice = candidate;
                resource = product.Resource;
            }
        }
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
        foreach (var b in Workshop.Benches)
        {
            if (b.Def == choice.Definition)
            {
                selected.Ordinal++;
            }
        }
        resolved.Workshop = selected;
        resolved.Definition = choice.Definition;
        resolved.Environment = Pol.PlacementEnvironment.Indoors;
        resolved.Stuff = ColonyPlanningRules.BuildStuff(facts.Facts, ResourceStockRules.Count(facts.Snapshot.Resources), resolved.Definition);
        return (resolved, new Verdict());
    }
}

partial class BuildingPlanner
{
    /// <summary>
    /// Whether any generator definition the power family compiles is buildable now, so a powered bench can be
    /// staged and connected; a generator row the census did not describe leaves it unknown.
    /// </summary>
    internal static bool GeneratorAvailable(IReadOnlyDictionary<string, bool> available)
    {
        var result = false;
        foreach (var name in PowerRules.GeneratorDefinitions)
        {
            if (!available.TryGetValue(name, out var value))
                throw new RequiredFactException("generator definition:" + name);
            result |= value;
        }
        return result;
    }
}
