namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The batched preview a native source may offer: one call for every placement of a wave instead of one hop per cell.</summary>
internal interface IShellBatchPreviewer
{
    Task<(IReadOnlyList<BuildingPreview> Previews, BridgeResult Result)> PreviewBuildingsAsync(IReadOnlyList<DomainAction> actions, GenerationSnapshot snapshot, CancellationToken ct);
}

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>
    /// Prefers furnishing verified indoor space. Only when that whole method has no space does it raise the
    /// layout plan's shelter room: the bunk rungs, then the ring through one reconcile.
    /// </summary>
    public static RoundsBuildingPlanner NewRoundsShelterPlanner(Rounder? reviewer, IRoundsBuildingSource? native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsShelterPlanner: reviewer == nil || native == nil");
        }
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = MaintainHousing, Phase = Pol.Phase.HousingShelter, Definition = "Wall", Shelter = true };
    }

    /// <summary>Reuses the same furnishing and ring path to keep one spare indoor sleeping place beyond the observed population.</summary>
    public static RoundsBuildingPlanner NewRoundsExpansionPlanner(Rounder? reviewer, IRoundsBuildingSource? native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsExpansionPlanner: reviewer == nil || native == nil");
        }
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = MaintainHousing, Phase = Pol.Phase.HousingExpansion, Definition = "Wall", Shelter = true };
    }

    /// <summary>The layout module this planner furnishes: a facility ladder's room role names it, the shelter and expansion planners raise the shelter.</summary>
    internal (Pol.PlannedRole Role, bool Ok) RoomModule() => PlannedRoleOf(RoomRole());

    /// <summary>The room role a planner sites for: a facility ladder's own role, and Shelter for the shelter and expansion planners, which raise the colony's temporary starter room.</summary>
    internal Pol.RoomRole RoomRole() => Facility is not null ? Facility.Role : Pol.RoomRole.Shelter;

    /// <summary>
    /// Whether the planner walks a facility ladder whose furniture stands in a planned room (comfort, workshop,
    /// hospital, sleeping, laboratory), as opposed to the initial shelter and expansion, whose ring is the
    /// deficit itself.
    /// </summary>
    internal bool FacilityLadder()
    {
        if (Concern == MaintainResource || Concern == MaintainEquipment || Concern == MaintainMedicalReserves || Concern == EnsureResearch)
        {
            return true;
        }
        return Phase == Pol.Phase.HousingSleeping || Phase == Pol.Phase.ComfortRanked;
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// A completed starter shell may trigger RimWorld's normal automatic roofing. Give that work at most four
    /// in-game hours from the durable completion tick. Polling, restarting or cancelling cannot renew this
    /// budget. Shells vary in size by shape, so any nonempty fully completed plan qualifies. The budget is
    /// scoped to the world and plan revision the walls were completed in, not to the native order generation:
    /// an authority re-acquisition between the last dispatch and the review (a cancelled transport call) moves
    /// the generation on without touching the standing walls, and nothing else lends the clock the roof needs
    /// when the shell is the only work.
    /// </summary>
    internal static uint ShelterNativeWorkTicks(Sto.PlanState plan, GenerationSnapshot current, Tick tick)
    {
        if (plan.Progress.Count == 0)
        {
            return 0;
        }
        long completed = 0;
        foreach (var p in plan.Progress)
        {
            var v = p.View;
            var known = v.Effect.TryGetValue(out var effect);
            if (v.Stage != Stage.Completed || v.Unresolved || !known || effect != Effect.Completed || !Boundary.World(v.Snapshot, current)
                || v.Snapshot.Plan != plan.Spec.Id || v.Snapshot.Revision != plan.Spec.Revision || v.Tick.Value > tick.Value)
            {
                return 0;
            }
            completed = Math.Max(completed, v.Tick.Value);
        }
        const long budget = 10000;
        if (tick.Value - completed >= budget)
        {
            return 0;
        }
        return (uint)(budget - (tick.Value - completed));
    }

    /// <summary>Whether every named definition can be raised by any colonist now (no construction skill needed); a shell piece also has to be a single cell.</summary>
    internal static bool RoundsDefinitionsAvailable(Observation.ColonyProjection facts, IReadOnlyList<string> names, bool shell) =>
        DefinitionsGate(facts, names, shell).IsZero;

    /// <summary>The verdict RoundsDefinitionsAvailable reads: the zero Verdict when every named definition is buildable, else the first thing missing, named.</summary>
    internal static Verdict DefinitionsGate(Observation.ColonyProjection facts, IReadOnlyList<string> names, bool shell)
    {
        foreach (var name in names)
        {
            var (def, found) = DefinitionRow(facts, name);
            if (!found)
            {
                return FieldUnavailable(name + "_definition");
            }
            var availability = DefinitionAvailability(def);
            if (!availability.IsZero)
            {
                return availability;
            }
            if (!def.ConstructionSkill.TryGetValue(out var skill))
            {
                return FieldUnavailable(name + "_construction_skill");
            }
            if (skill != 0)
            {
                return Refuse(Pol.Cause.NoWorker, "builder_for_" + name, string.Create(CultureInfo.InvariantCulture, $"construction_skill_{skill}"));
            }
            if (shell)
            {
                if (!def.Size.TryGetValue(out var size))
                {
                    return FieldUnavailable(name + "_size");
                }
                if (size.Width != 1 || size.Height != 1)
                {
                    return NoSpace(name + "_footprint");
                }
            }
        }
        return new Verdict();
    }

    internal static (Observation.PlanningDefinition Def, bool Found) DefinitionRow(Observation.ColonyProjection facts, string name)
    {
        foreach (var def in facts.Definitions)
        {
            if (def.Name == name)
            {
                return (def, true);
            }
        }
        return (new Observation.PlanningDefinition(), false);
    }

    /// <summary>The zero Verdict for a definition the game reports buildable, else the missing fact or the research it waits on.</summary>
    internal static Verdict DefinitionAvailability(Observation.PlanningDefinition def)
    {
        if (!def.Available.TryGetValue(out var ready))
        {
            return FieldUnavailable(def.Name + "_availability");
        }
        if (ready)
        {
            return new Verdict();
        }
        if (def.Research.Count == 0)
        {
            return AwaitingPlan(def.Name, "unavailable");
        }
        return ResearchWait(string.Join("+", def.Research));
    }

    internal static bool PositiveFact(Fact<bool> f) => f.TryGetValue(out var v) && v;

    /// <summary>
    /// Folds one more preview's stock into a bundle's. Previews of one bundle are native calls made one after
    /// another, and an Available count can differ between them (it is net of the native blueprint census and
    /// moves as colonists haul); the bundle is funded from the lowest value any preview saw, and an unknown one
    /// leaves the resource unknown. Returns the merged observation.
    /// </summary>
    internal static Pol.StockObservation MergeRoundsStock(Pol.StockObservation stock, Pol.StockObservation next, bool first)
    {
        var nativeConstruction = first ? next.NativeConstruction : stock.NativeConstruction && next.NativeConstruction;
        var values = stock.Values.ToList();
        var index = new Dictionary<Pol.Resource, int>();
        for (var i = 0; i < values.Count; i++)
        {
            index[values[i].Resource] = i;
        }
        var seen = new HashSet<Pol.Resource>();
        foreach (var v in next.Values)
        {
            if (!seen.Add(v.Resource))
            {
                throw new ControlException("mergeRoundsStock: seen[v.Resource]");
            }
            if (index.TryGetValue(v.Resource, out var at))
            {
                values[at] = values[at] with { Available = LowerStock(values[at].Available, v.Available) };
            }
            else
            {
                values.Add(v);
                index[v.Resource] = values.Count - 1;
            }
        }
        return stock with { NativeConstruction = nativeConstruction, Values = [.. values] };
    }

    internal static Fact<long> LowerStock(Fact<long> a, Fact<long> b)
    {
        if (!a.TryGetValue(out var x) || !b.TryGetValue(out var y))
        {
            return Unknown<long>();
        }
        return Known<long>(Math.Min(x, y));
    }
}
