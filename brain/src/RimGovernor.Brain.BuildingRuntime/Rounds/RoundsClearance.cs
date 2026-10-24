using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the home-upkeep family (#3253): one colony read gives the damaged-structure census, and when
/// clearance runs, the clearance census on the planned ground, its planned-ground work and the next clearance step
/// (the recovery queue's batch first, then the planned-ground step, with already designated targets dropped).
/// </summary>
public sealed partial class HomeUpkeepReader
{
    private readonly Rounder _reviewer;
    private readonly FacadeNativeClient _native;
    private readonly bool _repairs;
    private readonly bool _clearance;

    public HomeUpkeepReader(Rounder reviewer, FacadeNativeClient native, bool repairs, bool clearance)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("HomeUpkeepReader: reviewer == nil || native == nil");
        }
        _reviewer = reviewer;
        _native = native;
        _repairs = repairs;
        _clearance = clearance;
    }

    /// <summary>The family's scheduler entry: it wakes on its reads and on deconstruction, uninstall, roof and floor removal outcomes.</summary>
    public static FamilyRunner<HomeUpkeepFacts> Runner(Rounder reviewer, FacadeNativeClient native, bool repairs, bool clearance)
    {
        var reader = new HomeUpkeepReader(reviewer, native, repairs, clearance);
        return new FamilyRunner<HomeUpkeepFacts>(new HomeUpkeepFamily(repairs, clearance), reader.ReadAsync, Schedule);
    }

    /// <summary>An optional entry woken by the outcomes of repairs and of every clearance step kind.</summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Class = PlannerClass.Optional,
        Kinds = [ActionKind.Repair, ActionKind.Deconstruction, ActionKind.UninstallBuilding, ActionKind.RemoveRoof, ActionKind.FloorRemoval],
    };

    internal async Task<HomeUpkeepFacts> ReadAsync(FamilyReadScope scope, CancellationToken call)
    {
        if (scope.Immediate)
        {
            // No SeriousHarm Needs: the immediate pass reads nothing.
            return new HomeUpkeepFacts();
        }
        var review = _reviewer.Player.Journal.LoadRounds(call);
        var expected = scope.Scope ?? DecodeReads.DecodeIdentity(await _native.IdentityAsync(call).ConfigureAwait(false));
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("home upkeep: the native boundary moved");
        }
        // Clearance reads the room census: retired walls wait while they still bound a standing room (#3681).
        var colony = _clearance
            ? (await _reviewer.ObserveRoomsAsync(_native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false)).Reading
            : await _reviewer.ObserveColonyAsync(_native, expected, null, call).ConfigureAwait(false);
        var facts = new HomeUpkeepFacts();
        if (_repairs)
        {
            facts = facts with { Structures = colony.Projection.Facts.Upkeep.Structures };
        }
        if (!_clearance)
        {
            return facts;
        }
        var ground = Rounder.PlannedGround(colony.Projection);
        var census = await _native.ReadClearanceAsync(expected, true, ground, call).ConfigureAwait(false);
        var (others, player) = ClearanceGroundRules.SplitGroundRows(ClearanceReads.Targets(census).Concat(GroundBlueprints(colony.Projection) ?? []), colony.Projection.Facts.Preferences);
        var wants = _reviewer.ClearFloors(colony.Projection);
        facts = facts with
        {
            Clearance = Known<EquatableArray<ClearanceTarget>>([.. others]),
            Ground = Known<EquatableArray<string>>([.. PlannedGroundWork(colony.Projection, player, census.Floors, wants)]),
        };
        var step = await RecoveryStepAsync(Boundary.Identity(scope.World), review.RecoveryQueue, others, colony.Projection, call).ConfigureAwait(false);
        Func<PlanId, GroundStep, (string Prefix, IReadOnlyList<DomainAction> Actions)> method;
        if (step.Roof.Count > 0 || step.Targets.Count > 0)
        {
            step = PendingClearanceStep(step);
            if (step.Roof.Count + step.Targets.Count == 0)
            {
                return facts;
            }
            method = RecoveryStepMethod;
        }
        else
        {
            var (planned, ok) = PlannedGroundStep(colony.Projection, StampPacking(player, colony.Projection), census.Floors, wants);
            if (!ok)
            {
                return facts;
            }
            step = PendingClearanceStep(planned);
            if (step.Roof.Count + step.Targets.Count + step.Floors.Count == 0)
            {
                return facts;
            }
            method = GroundStepMethod;
        }
        // The MethodId names the step, not the plan: the same step continues its open Method.
        var (prefix, probe) = method(PlanIds.MintPlanId(), step);
        var chosen = step;
        return facts with
        {
            Candidate = new ClearanceCandidate(
                new MethodId(prefix.EndsWith('-') ? prefix[..^1] : prefix),
                probe.Any(a => a.Deconstruction is { ReplacesWithWall: true }),
                id => method(id, chosen).Actions),
        };
    }
}

partial class HomeUpkeepReader
{
    /// <summary>The step with the already designated targets and floors dropped.</summary>
    internal static GroundStep PendingClearanceStep(GroundStep step) =>
        step with
        {
            Targets = [.. step.Targets.Where(t => !t.Designated)],
            Floors = [.. step.Floors.Where(f => !f.Designated)],
        };

    internal static (GroundStep Step, bool Ok) PlannedGroundStep(ColonyProjection colony, IReadOnlyList<ClearanceTarget> player, IReadOnlyList<ClearanceFloor> floors, RoomFloors wants)
    {
        if (!colony.LayoutPlan.TryGetValue(out var plan))
        {
            return (new GroundStep(), false);
        }
        var (ground, known) = RoundsBuildingPlanner.ColonyGround(colony);
        if (!known || !colony.Rooms.TryGetValue(out var rooms))
        {
            return (new GroundStep(), false);
        }
        var planned = ClearanceReconcileRules.PlannedGroundStep(plan, ground, player, floors, rooms, Rounder.RetiredGroundOfColony(colony), wants);
        return planned is null ? (new GroundStep(), false) : (planned, true);
    }

    internal static IReadOnlyList<string> PlannedGroundWork(ColonyProjection colony, IReadOnlyList<ClearanceTarget> player, IReadOnlyList<ClearanceFloor> floors, RoomFloors wants)
    {
        if (!colony.LayoutPlan.TryGetValue(out var plan))
        {
            return [];
        }
        var (ground, known) = RoundsBuildingPlanner.ColonyGround(colony);
        if (!known || !colony.Rooms.TryGetValue(out var rooms))
        {
            return [];
        }
        return ClearanceReconcileRules.PlannedGroundWork(plan, ground, player, floors, rooms, Rounder.RetiredGroundOfColony(colony), wants);
    }

    /// <summary>
    /// The colony's player blueprints as clearance rows (#3742): the clearance census reports buildings only (a frame
    /// is one), so retired-ground clearance reads the blueprints from the construction census and cancels them. Null
    /// while that census is incomplete.
    /// </summary>
    internal static List<ClearanceTarget>? GroundBlueprints(ColonyProjection colony)
    {
        if (!colony.Facts.CurrentConstruction.TryGetValue(out var census) || !census.Colony)
        {
            return null;
        }
        var sizes = Rounder.DefinitionSizes(colony);
        var output = new List<ClearanceTarget>();
        foreach (var site in census.Sites)
        {
            if (site.Stage != ConstructionSite.BlueprintStage || site.Building is not { } b || site.Id.Length == 0)
            {
                continue;
            }
            var r = Rounder.SiteRect(sizes, b);
            output.Add(new ClearanceTarget
            {
                EntityId = site.Id,
                DefName = b.Definition,
                Minimum = new Cell(r.X, r.Z),
                Maximum = new Cell(r.X + r.Width - 1, r.Z + r.Height - 1),
                Player = true,
                Blueprint = true,
            });
        }
        return output;
    }

    /// <summary>Stamps the packable flag and the in-use flag (a bed with owners or a bench with an active bill) onto the rows.</summary>
    internal static List<ClearanceTarget> StampPacking(IReadOnlyList<ClearanceTarget> rows, ColonyProjection colony)
    {
        var inUse = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (colony.Facts.Sleeping.TryGetValue(out var sleeping))
        {
            foreach (var bed in sleeping.Beds)
            {
                inUse[bed.Id] = (inUse.TryGetValue(bed.Id, out var was) && was) || bed.Owners.Count > 0;
            }
        }
        if (colony.ProductionBenches.TryGetValue(out var benches))
        {
            foreach (var bench in benches)
            {
                foreach (var bill in bench.Bills)
                {
                    if (bill.Active.TryGetValue(out var active) && active)
                    {
                        inUse[bench.Id] = true;
                    }
                }
            }
        }
        var output = new List<ClearanceTarget>(rows.Count);
        foreach (var row in rows)
        {
            output.Add(row with
            {
                Packable = colony.Packable.Contains(row.DefName),
                InUse = inUse.TryGetValue(row.EntityId, out var used) && used,
            });
        }
        return output;
    }

    internal static RoomObservation ColonyRooms(ColonyProjection colony)
    {
        colony.Rooms.TryGetValue(out var rooms);
        return rooms!;
    }

    internal static (string Prefix, IReadOnlyList<DomainAction> Actions) GroundStepMethod(PlanId id, GroundStep step)
    {
        string prefix;
        if (step.Phase == GroundPhase.Blueprints)
        {
            prefix = BatchPrefix("cancel", step.Targets[0].EntityId, step.Targets.Count);
        }
        else if (step.Phase == GroundPhase.Furniture)
        {
            prefix = $"deconstruct-{step.Targets[0].EntityId}-";
        }
        else if (step.Phase == GroundPhase.Pack)
        {
            prefix = BatchPrefix("pack", step.Targets[0].EntityId, step.Targets.Count);
        }
        else if (step.Phase == GroundPhase.Doors)
        {
            prefix = $"swap-door-{step.Targets[0].EntityId}-";
        }
        else if (step.Phase == GroundPhase.Walls)
        {
            if (step.Roof.Count > 0 && step.Targets.Count == 0)
            {
                prefix = string.Create(CultureInfo.InvariantCulture, $"roof-off-{step.Roof[0].X}-{step.Roof[0].Z}-x{step.Roof.Count}-");
            }
            else if (!step.Ground.Empty())
            {
                var b = step.Ground.Bounds();
                prefix = string.Create(CultureInfo.InvariantCulture, $"ground-walls-{b.X}-{b.Z}-");
            }
            else
            {
                prefix = BatchPrefix("ground-walls", step.Targets[0].EntityId, step.Targets.Count);
            }
        }
        else
        {
            var c = step.Floors[0].Cell;
            prefix = string.Create(CultureInfo.InvariantCulture, $"ground-floors-{c.X}-{c.Z}-x{step.Floors.Count}-");
        }
        return (prefix, GroundActions(id, step));
    }

    internal static string BatchPrefix(string kind, string first, int n) =>
        n > 1 ? string.Create(CultureInfo.InvariantCulture, $"{kind}-{first}-x{n}-") : $"{kind}-{first}-";

    internal static IReadOnlyList<DomainAction> GroundActions(PlanId id, GroundStep step)
    {
        var actions = new List<DomainAction>();
        ActionId Next() => new(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}"));
        if (step.Roof.Count > 0)
        {
            actions.Add(DomainAction.NewRemoveRoofAction(Next(), new RemoveRoof(step.Roof)));
        }
        foreach (var target in step.Targets)
        {
            if (step.Phase == GroundPhase.Pack)
            {
                var move = new MoveBuilding(target.EntityId, target.DefName, target.Minimum, Rotation.South);
                actions.Add(DomainAction.NewUninstallBuildingAction(Next(), move));
                continue;
            }
            var value = new Deconstruction(target.EntityId, target.DefName, target.Minimum);
            if (step.Phase == GroundPhase.Doors)
            {
                value = value.WithWallReplacement();
            }
            else if (target.Blueprint)
            {
                value = value.WithBlueprintCancel();
            }
            actions.Add(DomainAction.NewDeconstructionAction(Next(), value));
        }
        foreach (var floor in step.Floors)
        {
            actions.Add(DomainAction.NewFloorRemovalAction(Next(), new FloorRemoval(floor.DefName, floor.Cell)));
        }
        return actions;
    }
}
