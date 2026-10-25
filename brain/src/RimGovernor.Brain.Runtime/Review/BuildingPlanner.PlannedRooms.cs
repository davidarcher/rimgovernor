using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

public sealed partial class BuildingPlanner
{
    /// <summary>Routes a station to the room whose saved layout contains that definition, whatever native role its other stations win.</summary>
    internal async Task<(BuildingPlanner? Planner, BuildingResult Result, bool Done)> PlannedFacilityFurnishingAsync(ControlState state, ReviewState review, NeedOwner goal, ColonyReading reading, ColonyReading facts, CancellationToken call, CancellationToken epoch)
    {
        if (!FacilityLadder() && !ButcherFacility() && Concern != FoodRules.CookingNeed.FilingKey) return (this, new BuildingResult(), false);
        var layout = facts.Facts.LayoutPlan;
        var room = layout is null ? null : PlannedStationRoom(layout, Definition, facts.Facts.CurrentConstruction);
        if (room is null)
            return (null, new BuildingResult { Verdict = AwaitingPlan("station_room", Definition) }, true);

        var result = await ReconcileRoomAsync(state, review, goal,
            new ReviewReading { Reading = reading, Emergency = new EmergencyFacts(), Frame = new BridgeReviewFrame() }, null,
            new RoomReconcile { RingOnly = true, Room = room, Name = BuildingPlanner.PlannedRoomMethod(room).Value }, call, epoch).ConfigureAwait(false);
        if (!result.Verdict.SkipsToPlacement) return (null, result, true);

        var placed = Copy();
        placed.Cells = BuildingPlanner.PlannedRoomInterior(room);
        placed.Environment = PlacementEnvironment.Anywhere;
        return (placed, new BuildingResult(), false);
    }

    internal static PlannedRoom? PlannedStationRoom(LayoutPlan layout, string definition, CurrentConstruction census) =>
        layout.Rooms.OrderBy(r => r.Id).FirstOrDefault(room =>
            (layout.Pieces.GetValueOrDefault(room.Id) ?? []).Count(p => p.Def == definition)
            > census.Buildings.Count(b => b.Building.Definition == definition && room.Region.Cells().Contains(b.Building.Cell))
                + census.Sites.Count(s => s.Building is { } b && b.Definition == definition && room.Region.Cells().Contains(b.Cell)));
}

partial class BuildingPlanner
{
    internal static (LayoutPlan Plan, RoomObservation Rooms, bool Known) PlannedLayout(ColonyReading facts)
    {
        var plan = facts.Facts.LayoutPlan;
        var pk = plan is not null;
        var rooms = facts.Facts.Upkeep.Rooms;
        return (plan!, rooms!, pk);
    }

    internal static (PlannedRoom Room, bool Owed) PlannedRoomOwed(ColonyReading facts, PlannedRole module)
    {
        var plan = facts.Facts.LayoutPlan;
        var known = plan is not null;
        var ground = ColonyGround(facts);
        if (!known)
        {
            return (default!, false);
        }

        return plan!.NextPlannedRoom(module, ground) is { } next ? (next, true) : (default!, false);
    }

    internal static GroundCensus ColonyGround(ColonyReading facts) =>
        LayoutGroundRules.GroundOf(facts.Facts.CurrentConstruction.Buildings);

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
        new(SleepingUpkeepPlanner.RoomReconcileName(Wire.Name(room.Role), room));

    internal static IReadOnlyList<InteriorRoom> PlannedInteriorRooms(ColonyReading facts, Func<PlannedRoom, bool> keep)
    {
        if (facts.Facts.LayoutPlan is not { } plan)
        {
            return [];
        }

        var edifice = new Dictionary<Cell, string>();
        foreach (var c in facts.PlanningWindow.Cells)
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

            var (room, ok) = InteriorRules.InteriorRoomFromLayout(planned, facts.Facts.Shapes);
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

    internal static IReadOnlyList<RoleMismatch> RoleMismatches(ColonyReading facts)
    {
        var (plan, rooms, known) = PlannedLayout(facts);
        var construction = facts.Facts.CurrentConstruction;
        var sleeping = facts.Facts.Sleeping;
        if (!known || construction is null || facts.Facts.Stations.Stations.Count == 0)
        {
            return [];
        }

        var contents = RoomRoleRules.ObservedRoomContents(facts.Facts.Stations, plan, rooms, construction.Buildings, sleeping!);
        return RoomRoleRules.GuardNativeRoles(facts.Facts.Stations, plan, rooms, contents);
    }

    internal static void RecordRoleMismatches(ColonyReading facts, CancellationToken ct)
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
