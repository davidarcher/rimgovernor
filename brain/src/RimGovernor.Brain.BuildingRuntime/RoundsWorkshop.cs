namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Adds the fresh bench and recipe-catalog reads the workshop planner needs on top of the building source.</summary>
public interface IRoundsWorkshopSource : IRoundsBuildingSource
{
    (IReadOnlyList<Bridge.GearBenchRead> Benches, BridgeResult Result) ReadGearBenches(CancellationToken ct, CommonPb.Identity identity);
}

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
    /// <summary>
    /// Shares the facility ladder between replacement gear and resource targets. An admitted project or
    /// research prerequisite owns this step.
    /// </summary>
    internal RoundsBuildingResult StepWorkshops(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var gear = Copy();
        gear.Concern = MaintainEquipment;
        var result = gear.Step(call, epoch, arbiter);
        if (result.Decision.Admitted || result.NativeWorkTicks > 0 || result.Verdict == BuildingWorkshopResearch)
        {
            return result;
        }
        var resource = Step(call, epoch, arbiter);
        if (resource.Verdict == BuildingReasonNoDeficit || resource.Verdict == BuildingReasonDisabled)
        {
            return result;
        }
        return resource;
    }

    /// <summary>
    /// Stages a production bench for a resource deficit. The scheduler also runs its ladder for replacement
    /// gear. For a product no existing bench can produce: it discovers which player-buildable benches host a
    /// recipe for the resource, records the research still gating the first of them (the review turns that into
    /// EnsureResearch's target), furnishes a room whose native role can host a Workshop, and when no such room
    /// exists stages a starter shell first, the same ladder EnsureComfort walks. A powered bench is staged when
    /// a generator definition is available; EnsureBasicPower then connects it as an unpowered consumer. Bills on
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
        if (native is not IRoundsWorkshopSource)
        {
            throw new ControlException("NewRoundsWorkshopPlanner: !ok");
        }
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = MaintainResource };
    }

    /// <summary>
    /// Reads the deficit resource, the current bench census and the recipe catalog before the planning census
    /// is requested, so the census can describe exactly the candidate bench definitions. A non-zero verdict ends
    /// the step.
    /// </summary>
    internal (WorkshopSelection? Selection, Verdict Verdict) PrepareWorkshop(CancellationToken call, ControlState state, Sto.Rounds review)
    {
        if (Concern != MaintainEquipment)
        {
            var wanted = Reviewer.ResourceTargets(state.Snapshot);
            if (wanted.Count == 0)
            {
                return (null, BuildingReasonDisabled);
            }
        }
        if (Native is not IRoundsWorkshopSource source)
        {
            throw new ControlException("prepareWorkshop: !ok");
        }
        var identity = BoundaryApi.Identity(state.Snapshot);
        var (reply, _) = source.ReadColonyFacts(call, identity, Concern == MaintainEquipment);
        var observed = reply.Observed;
        if (observed is null || !BridgePackage.ColonyFactsValid(observed, identity))
        {
            throw new ControlException("prepareWorkshop: observed == nil || bridge.ValidateColonyFacts(observed, identity) != nil");
        }
        try
        {
            BoundaryApi.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("prepareWorkshop: err != nil || observed.Context.GetTick() < int64(review.Tick)");
        }
        if (observed.Context.Tick < review.Tick.Value)
        {
            throw new ControlException("prepareWorkshop: err != nil || observed.Context.GetTick() < int64(review.Tick)");
        }
        var defs = GearDefinitions(call, Native, identity);
        if (defs.Catalog is null || !defs.Finished.TryGetValue(out var finished))
        {
            throw new ControlException("prepareWorkshop: the recipe hosts need the definition catalog and the finished research");
        }
        Pol.Resource resource;
        List<Pol.Resource> products = [];
        if (Concern == MaintainEquipment)
        {
            var gear = observed.Planning?.Observed?.Gear;
            if (gear is null)
            {
                return (null, FieldUnavailable("gear_census"));
            }
            var things = FrameThings(call, Native, identity);
            if (!GearObservationFacts(observed, new Bridge.Tables { Things = things }, defs).TryGetValue(out var facts))
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
            var needs = GearReplacementNeeds(Known<Pol.GearObservation>(facts));
            if (needs.Count == 0)
            {
                return (null, BuildingReasonNoDeficit);
            }
            resource = needs[0];
            products = [.. needs];
        }
        else
        {
            var stock = ResourceStockFacts(observed);
            var wanted = Reviewer.ResourceTargets(state.Snapshot);
            var (picked, _, found) = SelectResourceTarget(wanted, stock);
            if (!found)
            {
                return (null, BuildingReasonNoDeficit);
            }
            resource = picked;
        }
        // The Go source holds the read error until after the Beer branch; a failed census read surfaces there.
        var census = (source.ReadGearBenches(call, identity)).Benches;
        if (resource.ToString() == "Beer")
        {
            if (observed.FermentingBarrels is null)
            {
                return (null, FieldUnavailable("fermenting_barrels"));
            }
            if (observed.FermentingBarrels == 0)
            {
                var barrel = new WorkshopSelection { Barrel = true, Resource = resource, Candidates = ["FermentingBarrel"] };
                return (barrel, new Verdict());
            }
            var items = Reviewer.ItemFacts(call, state.Snapshot);
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
            var request = new Pol.WorkshopRequest { Resource = product, Benches = Known<IReadOnlyList<Pol.GearBench>>(benches), Hosts = hosts, Further = selection.Further };
            SnapshotPackage.NoteWorkshop(call, request);
            var choice = SelectWorkshopBench(request);
            if (choice.Method == Pol.WorkshopMethod.Existing)
            {
                RecordWorkshopLadder(call, state, review, product, choice);
                return (null, BuildingExistingFacility);
            }
            var candidatesFor = WorkshopResearchCandidates(product, hosts);
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
            return (null, BuildingWorkshopUnavailable);
        }
        selection.Candidates = [.. gated.OrderBy(n => n, StringComparer.Ordinal)];
        selection.Candidates.AddRange(GeneratorDefinitions);
        return (selection, new Verdict());
    }

    /// <summary>
    /// Persists the rung the workshop settled on so the next rounds can raise EnsureResearch for a
    /// research-gated bench, or clear that need once the bench is buildable or standing.
    /// </summary>
    internal void RecordWorkshopLadder(CancellationToken call, ControlState state, Sto.Rounds review, Pol.Resource resource, Pol.WorkshopChoice choice)
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
        Reviewer.Player.Journal.SaveProductionLadder(call, record);
    }

    /// <summary>
    /// Resolves the prepared candidates against the planning census the same way SelectComfort resolves a
    /// comfort method: the chosen bench becomes the definition, placed indoors in a Workshop-hosting room.
    /// </summary>
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectWorkshop(CancellationToken call, ControlState state, Sto.Rounds review, Observation.ColonyProjection facts)
    {
        if (Workshop is null)
        {
            return (null, FieldUnavailable("workshop"));
        }
        if (Workshop.Barrel)
        {
            if (!facts.DefinitionAvailable("FermentingBarrel").TryGetValue(out var barrelAvailable) || !barrelAvailable)
            {
                return (null, BuildingWorkshopUnavailable);
            }
            var barrelFacility = Facility(Pol.RoomRole.Workshop);
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
                Benches = Known<IReadOnlyList<Pol.GearBench>>(Workshop.Benches),
                Hosts = product.Hosts,
                Definitions = definitions,
                Power = GeneratorAvailable(available),
                BuilderSkill = BuilderSkill(facts.WorkPawns),
                Further = Workshop.Further,
            };
            SnapshotPackage.NoteWorkshop(call, request);
            var candidate = SelectWorkshopBench(request);
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
            RecordWorkshopLadder(call, state, review, resource, choice);
        }
        switch (choice.Method)
        {
            case Pol.WorkshopMethod.Unknown:
                return (null, FieldUnavailable("workshop"));
            case Pol.WorkshopMethod.Existing:
                return (null, BuildingExistingFacility);
            case Pol.WorkshopMethod.Research:
                return (null, BuildingWorkshopResearch);
            case Pol.WorkshopMethod.Unavailable:
                return (null, BuildingWorkshopUnavailable);
        }
        var facility = Facility(Pol.RoomRole.Workshop);
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

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// Whether any generator definition the power family compiles is buildable now, so a powered bench can be
    /// staged and connected; a generator row the census did not describe leaves it unknown.
    /// </summary>
    internal static Fact<bool> GeneratorAvailable(IReadOnlyDictionary<string, Fact<bool>> available)
    {
        var result = Known<bool>(false);
        foreach (var name in GeneratorDefinitions)
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
