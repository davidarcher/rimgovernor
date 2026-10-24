using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    private static readonly HashSet<RoomRole> FacilityFurnishingRoles =
    [
        RoomRole.DiningRoom, RoomRole.RecRoom, RoomRole.Hospital, RoomRole.Laboratory, RoomRole.Workshop,
    ];

    internal (PlannedRole Role, bool Ok) PlannedRoomModule()
    {
        if (Concern == FoodRules.CookingNeed.FilingKey && Paste.Count == 0)
        {
            return (PlannedRole.Kitchen, true);
        }

        if (ButcherFacility() && RoundsBuildingPlanner.ButcherBenchDefinition(Definition))
        {
            return (PlannedRole.Butchery, true);
        }

        return (default, false);
    }

    /// <summary>Furnishes a planned facility room: reconciles its shell first when owed, then resolves to the interior cells.</summary>
    internal async Task<(RoundsBuildingPlanner? Planner, RoundsBuildingResult Result, bool Done)> PlannedFacilityFurnishingAsync(ControlState state, Rounds review, NeedOwner goal, ColonyReading reading, ColonyProjection facts, CancellationToken call, CancellationToken epoch)
    {
        if (Facility is null || Cells is not null || !FacilityFurnishingRoles.Contains(Facility.Role))
        {
            return (this, new RoundsBuildingResult(), false);
        }

        var (module, ok) = RoomModule();
        if (!ok)
        {
            return (this, new RoundsBuildingResult(), false);
        }

        var (room, owed) = RoundsBuildingPlanner.PlannedRoomOwed(facts, module);
        if (!owed)
        {
            var cells = RoundsBuildingPlanner.PlannedRoomCells(facts, module);
            if (cells is null)
            {
                return (this, new RoundsBuildingResult(), false);
            }

            var furnish = Copy();
            furnish.Facility = null;
            furnish.Cells = cells;
            return (furnish, new RoundsBuildingResult(), false);
        }

        var result = await ReconcileRoomAsync(state, review, goal, new RoundsReading { Reading = reading, Emergency = new EmergencyFacts(), Frame = new BridgeRoundsFrame() }, null, new RoomReconcile { RingOnly = true, Room = room, Name = RoundsBuildingPlanner.PlannedRoomMethod(room).Value }, call, epoch).ConfigureAwait(false);
        if (!result.Verdict.SkipsToPlacement)
        {
            return (null, result, true);
        }

        var placed = Copy();
        placed.Facility = null;
        placed.Cells = RoundsBuildingPlanner.PlannedRoomInterior(room);
        placed.Environment = PlacementEnvironment.Anywhere;
        return (placed, new RoundsBuildingResult(), false);
    }

    /// <summary>Whether the planner's furniture stands on the planned room's interior: the kitchen's stove or campfire and the butchery's bench.</summary>
    internal static bool HostsInPlannedRoom(PlannedRole module) => module == PlannedRole.Kitchen || module == PlannedRole.Butchery;
}

partial class RoundsBuildingPlanner
{
    internal static (LayoutPlan Plan, RoomObservation Rooms, bool Known) PlannedLayout(ColonyProjection facts)
    {
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        return (plan!, rooms!, pk && rk);
    }

    internal static (PlannedRoom Room, bool Owed) PlannedRoomOwed(ColonyProjection facts, PlannedRole module)
    {
        var known = facts.LayoutPlan.TryGetValue(out var plan);
        var (ground, groundKnown) = ColonyGround(facts);
        if (!known || !groundKnown)
        {
            return (default!, false);
        }

        return plan!.NextPlannedRoom(module, ground) is { } next ? (next, true) : (default!, false);
    }

    internal static (GroundCensus Ground, bool Known) ColonyGround(ColonyProjection facts)
    {
        if (!facts.Facts.CurrentConstruction.TryGetValue(out var construction) || !construction.Colony)
        {
            return (default!, false);
        }

        return (LayoutGroundRules.GroundOf(construction.Buildings), true);
    }

    internal static IReadOnlyList<Cell>? PlannedRoomCells(ColonyProjection facts, PlannedRole module)
    {
        var (plan, rooms, known) = PlannedLayout(facts);
        if (!known)
        {
            return null;
        }

        foreach (var r in plan.Rooms)
        {
            if (r.Role != module)
            {
                continue;
            }

            var (_, ok) = PlannedRoom.CensusRoomIn(r, rooms);
            if (!ok)
            {
                continue;
            }

            return RectCells(r.Region.Bounds());
        }

        return null;
    }

    internal static IReadOnlyList<Cell> PlannedRoomInterior(PlannedRoom room) => RectCells(room.Region.Bounds());

    internal static IReadOnlyList<Cell> RectCells(Rectangle input)
    {
        var cells = new List<Cell>(Math.Max(0, input.Width * input.Height));
        for (var z = input.Z; z < input.Z + input.Height; z++)
        {
            for (var x = input.X; x < input.X + input.Width; x++)
            {
                cells.Add(new Cell(x, z));
            }
        }

        return cells;
    }

    internal static MethodId PlannedRoomMethod(PlannedRoom room) =>
        new(RoundsSleepingUpkeepPlanner.RoomReconcileName(Wire.Name(room.Role), room));

    internal static IReadOnlyList<InteriorRoom> PlannedInteriorRooms(ColonyProjection facts, Func<PlannedRoom, bool> keep)
    {
        if (!facts.LayoutPlan.TryGetValue(out var plan))
        {
            return [];
        }

        var edifice = new Dictionary<Cell, string>();
        foreach (var c in facts.Cells)
        {
            var d = c.PlayerEdifice();
            if (d != "")
            {
                edifice[c.Cell] = d;
            }
        }

        var output = new List<InteriorRoom>();
        foreach (var planned in plan.Rooms)
        {
            if (!keep(planned))
            {
                continue;
            }

            var (room, ok) = InteriorRules.InteriorRoomFromLayout(planned, facts.Shapes);
            if (!ok || room is null)
            {
                continue;
            }

            var seen = new HashSet<string>();
            var standing = new List<string>(room.Standing);
            foreach (var c in PlannedRoomInterior(planned))
            {
                if (edifice.TryGetValue(c, out var d) && seen.Add(d))
                {
                    standing.Add(d);
                }
            }

            standing.Sort(StringComparer.Ordinal);
            output.Add(room with { Standing = standing });
        }

        return output;
    }

    internal static IReadOnlyList<Cell>? PlannedRoomPlanCells(ColonyProjection facts, PlannedRole module)
    {
        if (!facts.LayoutPlan.TryGetValue(out var plan))
        {
            return null;
        }

        foreach (var r in plan.Rooms)
        {
            if (r.Role == module)
            {
                return PlannedRoomInterior(r);
            }
        }

        return null;
    }

    internal static IReadOnlyList<RoleMismatch> RoleMismatches(ColonyProjection facts)
    {
        var (plan, rooms, known) = PlannedLayout(facts);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var construction);
        var sk = facts.Facts.Sleeping.TryGetValue(out var sleeping);
        if (!known || !ck || !construction!.Colony || !sk || facts.Stations.Stations.Count == 0)
        {
            return [];
        }

        var contents = RoomRoleRules.ObservedRoomContents(facts.Stations, plan, rooms, construction.Buildings, sleeping!);
        return RoomRoleRules.GuardNativeRoles(facts.Stations, plan, rooms, contents);
    }

    internal static void RecordRoleMismatches(ColonyProjection facts, CancellationToken ct)
    {
        IReadOnlyList<RoleMismatch> mismatches;
        try
        {
            mismatches = RoleMismatches(facts);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Tel.Decide(new Decision
            {
                Kind = "layout_role_guard",
                Component = "layout",
                Level = TelemetryLevel.Warn,
                Verdict = "failed",
                Reason = "guard_error",
                Target = "rooms",
                Attrs = new Dictionary<string, object?> { ["error"] = e },
            });
            return;
        }

        foreach (var m in mismatches)
        {
            var scores = m.Scores.ToDictionary(kv => Wire.Name(kv.Key), kv => kv.Value);
            Tel.Decide(new Decision
            {
                Kind = "layout_role_guard",
                Component = "layout",
                Verdict = "mismatch",
                Reason = RoomRoleRules.RoleMismatchReason,
                Target = m.Room.ToString(CultureInfo.InvariantCulture),
                Attrs = new Dictionary<string, object?> { ["native"] = Wire.Name(m.Native), ["predicted"] = Wire.Name(m.Predicted), ["scores"] = scores },
            });
        }
    }
}
