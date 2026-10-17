using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

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

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>The workshop ladder for replacement gear, owned by the military/equipment Need (#3259).</summary>
    public static RoundsBuildingPlanner NewRoundsGearWorkshopPlanner(Rounder? reviewer, IRoundsBuildingSource? native)
    {
        var planner = NewRoundsWorkshopPlanner(reviewer, native);
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
    public static RoundsBuildingPlanner NewRoundsWorkshopPlanner(Rounder? reviewer, IRoundsBuildingSource? native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsWorkshopPlanner: reviewer == nil || native == nil");
        }
        if (native is not Observation.IRoundsSource)
        {
            throw new ControlException("NewRoundsWorkshopPlanner: !ok");
        }
        if (native is not FacadeNativeClient)
        {
            throw new ControlException("NewRoundsWorkshopPlanner: !ok");
        }
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = EconomyFamily.Resource.FilingKey, Need = EconomyFamily.Resource };
    }

    /// <summary>
    /// Reads the deficit resource, the current bench census and the recipe catalog before the planning census
    /// is requested, so the census can describe exactly the candidate bench definitions. A non-zero verdict ends
    /// the step.
    /// </summary>
    internal async Task<(WorkshopSelection? Selection, Verdict Verdict)> PrepareWorkshopAsync(ControlState state, Sto.Rounds review, CancellationToken call)
    {
        if (!GearLadder())
        {
            var wanted = Reviewer.ResourceTargets(state.Snapshot);
            if (wanted.Count == 0)
            {
                return (null, BuildingReasonDisabled);
            }
        }
        if (Native is not FacadeNativeClient source)
        {
            throw new ControlException("prepareWorkshop: !ok");
        }
        var identity = Boundary.Identity(state.Snapshot);
        var reply = await source.ReadColonyFactsAsync(identity, GearLadder(), call).ConfigureAwait(false);
        var observed = reply.Outcome?.Observed;
        if (observed is null)
        {
            throw new ControlException("prepareWorkshop: observed == nil");
        }
        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("prepareWorkshop: err != nil || observed.Context.GetTick() < int64(review.Tick)");
        }
        if ((observed.Context?.Tick).GetValueOrDefault() < review.Tick.Value)
        {
            throw new ControlException("prepareWorkshop: err != nil || observed.Context.GetTick() < int64(review.Tick)");
        }
        var defs = await RoundsGear.GearDefinitionsOfAsync(Native, identity, call).ConfigureAwait(false);
        if (defs.Catalog is null || !defs.Finished.TryGetValue(out var finished))
        {
            throw new ControlException("prepareWorkshop: the recipe hosts need the definition catalog and the finished research");
        }
        Pol.Resource resource;
        List<Pol.Resource> products = [];
        if (GearLadder())
        {
            var gear = observed.Planning?.Outcome?.Observed?.Gear;
            if (gear is null)
            {
                return (null, FieldUnavailable("gear_census"));
            }
            var things = await RoundsGear.FrameThingsOfAsync(Native, identity, call).ConfigureAwait(false);
            var (facts, factsKnown) = RoundsGear.GearObservationFacts(observed, new Tables(Buildings.Of(), Pawns.Of(), things), defs);
            if (!factsKnown)
            {
                return (null, FieldUnavailable("gear_census"));
            }
            foreach (var pawn in facts.Pawns)
            {
                if (!pawn.Candidates.TryGetValue(out var candidates) || candidates.Count > 0)
                {
                    return (null, BuildingReasonExistingWork);
                }
            }
            var needs = GearRules.GearReplacementNeeds(Known<Pol.GearObservation>(facts));
            if (needs.Count == 0)
            {
                return (null, BuildingReasonNoDeficit);
            }
            resource = needs[0];
            products = [.. needs];
        }
        else
        {
            var stock = RoundsResourcePlanner.ResourceStockFacts(observed);
            var wanted = Reviewer.ResourceTargets(state.Snapshot);
            var (picked, _, found) = ResourceRules.SelectResourceTarget(wanted, stock);
            if (!found)
            {
                return (null, BuildingReasonNoDeficit);
            }
            resource = picked;
        }
        // The Go source holds the read error until after the Beer branch; a failed census read surfaces there.
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
            var request = new Pol.WorkshopRequest { Resource = product, Benches = Known<EquatableArray<Pol.GearBench>>([.. benches]), Hosts = hosts, Further = selection.Further };
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
    internal void RecordWorkshopLadder(ControlState state, Sto.Rounds review, Pol.Resource resource, Pol.WorkshopChoice choice, CancellationToken call)
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
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectWorkshop(ControlState state, Sto.Rounds review, Observation.ColonyProjection facts, CancellationToken call)
    {
        if (Workshop is null)
        {
            return (null, FieldUnavailable("workshop"));
        }
        if (Workshop.Barrel)
        {
            if (!facts.DefinitionAvailable("FermentingBarrel").TryGetValue(out var barrelAvailable) || !barrelAvailable)
            {
                return (null, BuildingReasonWorkshopUnavailable);
            }
            var barrelFacility = Pol.RoomsRules.Facility(Pol.RoomRole.Workshop);
            var barrel = Copy();
            barrel.Definition = "FermentingBarrel";
            barrel.Environment = Pol.PlacementEnvironment.Indoors;
            barrel.Facility = barrelFacility;
            return (barrel, new Verdict());
        }
        var definitions = new List<Pol.BenchDefinition>(facts.Definitions.Count);
        var available = new Dictionary<string, Fact<bool>>(StringComparer.Ordinal);
        foreach (var d in facts.Definitions)
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
                Benches = Known<EquatableArray<Pol.GearBench>>([.. Workshop.Benches]),
                Hosts = product.Hosts,
                Definitions = definitions,
                Power = GeneratorAvailable(available),
                BuilderSkill = WorkRules.BuilderSkill(facts.WorkPawns),
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
        if (choice.Method != Pol.WorkshopMethod.Unknown)
        {
            RecordWorkshopLadder(state, review, resource, choice, call);
        }
        switch (choice.Method)
        {
            case Pol.WorkshopMethod.Unknown:
                return (null, FieldUnavailable("workshop"));
            case Pol.WorkshopMethod.Existing:
                return (null, BuildingReasonExistingFacility);
            case Pol.WorkshopMethod.Research:
                return (null, BuildingReasonWorkshopResearch);
            case Pol.WorkshopMethod.Unavailable:
                return (null, BuildingReasonWorkshopUnavailable);
        }
        var facility = Pol.RoomsRules.Facility(Pol.RoomRole.Workshop);
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
        resolved.Facility = facility;
        resolved.Stuff = facts.BuildStuff(resolved.Definition);
        return (resolved, new Verdict());
    }
}

partial class RoundsBuildingPlanner
{
    /// <summary>
    /// Whether any generator definition the power family compiles is buildable now, so a powered bench can be
    /// staged and connected; a generator row the census did not describe leaves it unknown.
    /// </summary>
    internal static Fact<bool> GeneratorAvailable(IReadOnlyDictionary<string, Fact<bool>> available)
    {
        var result = Known<bool>(false);
        foreach (var name in PowerRules.GeneratorDefinitions)
        {
            if (!available.TryGetValue(name, out var fact) || !fact.TryGetValue(out var v))
            {
                result = Unknown<bool>();
            }
            else if (v)
            {
                return Known<bool>(true);
            }
        }
        return result;
    }
}
