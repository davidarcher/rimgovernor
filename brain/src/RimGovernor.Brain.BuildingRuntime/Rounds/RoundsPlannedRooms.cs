namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    private static readonly HashSet<RoomRole> FacilityFurnishingRoles =
    [
        RoomRole.DiningRoom, RoomRole.RecRoom, RoomRole.Hospital, RoomRole.Laboratory, RoomRole.Workshop,
    ];

    internal (PlannedRole Role, bool Ok) PlannedRoomModule()
    {
        if (Concern == ConcernId.EnsureCooking && Paste.Count == 0)
        {
            return (PlannedRole.Kitchen, true);
        }

        if (ButcherFacility() && BuildingRuntimePackage.ButcherBenchDefinition(Definition))
        {
            return (PlannedRole.Butchery, true);
        }

        if (Concern == ConcernId.MaintainRefrigeration && Refrigeration is not null && Refrigeration.Method == RefrigerationMethod.Build)
        {
            return (PlannedRole.Freezer, true);
        }

        return (default, false);
    }

    /// <summary>Furnishes a planned facility room: reconciles its shell first when owed, then resolves to the interior cells.</summary>
    internal (RoundsBuildingPlanner? Planner, RoundsBuildingResult Result, bool Done) PlannedFacilityFurnishing(CancellationToken call, CancellationToken epoch, ControlState state, Rounds review, StandardState goal, ColonyReading reading, ColonyProjection facts)
    {
        if (Facility is null || Cells is not null || !FacilityFurnishingRoles.Contains(Facility.Role))
        {
            return (this, default, false);
        }

        if (Facility.Role == RoomRole.Laboratory && !AdvancedLab && BuildingRuntimePackage.ResearchHostRooms(facts).Count > 0)
        {
            return (this, default, false);
        }

        var (module, ok) = RoomModule();
        if (!ok)
        {
            return (this, default, false);
        }

        var (room, owed) = BuildingRuntimePackage.PlannedRoomOwed(facts, module);
        if (!owed)
        {
            var cells = BuildingRuntimePackage.PlannedRoomCells(facts, module);
            if (cells is null)
            {
                return (this, default, false);
            }

            var furnish = Copy();
            furnish.Facility = null;
            furnish.Cells = cells;
            return (furnish, default, false);
        }

        var result = ReconcileRoom(call, epoch, state, review, goal, new RoundsReading(reading), null, new RoomReconcile { RingOnly = true, Room = room, Name = BuildingRuntimePackage.PlannedRoomMethod(room).Value });
        if (!result.Verdict.SkipsToPlacement())
        {
            return (null, result, true);
        }

        var placed = Copy();
        placed.Facility = null;
        placed.Cells = BuildingRuntimePackage.PlannedRoomInterior(room);
        placed.Environment = PlacementEnvironment.Anywhere;
        return (placed, default, false);
    }

    internal bool TakesShelterSlot(ColonyProjection facts)
    {
        var known = facts.LayoutPlan.TryGetValue(out var plan);
        if (Facility is not null)
        {
            return Facility.Role == RoomRole.Laboratory && !AdvancedLab && PolicyFunctions.ShelterHosts(Capability.Research);
        }

        if (CraftingSpot)
        {
            return PolicyFunctions.ShelterHosts(Capability.Craft);
        }

        if (Definition == PolicyFunctions.ShelterHeatDef)
        {
            var cooking = Concern == ConcernId.EnsureCooking && Paste.Count == 0;
            return known && plan!.Cold && PolicyFunctions.ShelterHosts(Capability.Heat) && (cooking || (Temperature is not null && Temperature.Method == TemperatureMethod.Heat));
        }

        if (Definition == PolicyFunctions.ShelterCoolDef)
        {
            return known && plan!.Hot && Temperature is not null && Temperature.Method == TemperatureMethod.Cool;
        }

        return false;
    }

    internal bool HostsInPlannedRoom(PlannedRole module, ColonyProjection facts) =>
        (module == PlannedRole.Kitchen || module == PlannedRole.Butchery) && !TakesShelterSlot(facts);
}

internal static partial class BuildingRuntimePackage
{
    internal const string CraftingSpotDefinition = PolicyConstants.CraftingSpotDefinition;

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

        return plan!.NextPlannedRoom(module, ground);
    }

    internal static (GroundCensus Ground, bool Known) ColonyGround(ColonyProjection facts)
    {
        if (!facts.Facts.CurrentConstruction.TryGetValue(out var construction) || !construction.Colony)
        {
            return (default!, false);
        }

        return (PolicyFunctions.GroundOfWithFlap(construction.Buildings, facts.Shapes.Furniture.AnimalFlap), true);
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

            var (_, ok) = PolicyFunctions.CensusRoomIn(r, rooms);
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

    internal static IReadOnlyList<SiteCell> PlannedInteriorSiteCells(ColonyProjection facts, IReadOnlyList<InteriorRoom> rooms)
    {
        var output = new List<SiteCell>();
        foreach (var c in facts.Cells)
        {
            foreach (var room in rooms)
            {
                var inner = room.Interior;
                if (c.Cell.X >= inner.X && c.Cell.X < inner.X + inner.Width && c.Cell.Z >= inner.Z && c.Cell.Z < inner.Z + inner.Height)
                {
                    output.Add(c);
                    break;
                }
            }
        }

        return output;
    }

    internal static MethodId PlannedRoomMethod(PlannedRoom room) =>
        new($"{room.Role}-shell-{room.Region.Bounds().X}-{room.Region.Bounds().Z}");

    internal static int ShelterCampfires(ColonyProjection facts) => PolicyFunctions.ShelterCampfires(facts.LayoutPlan.TryGetValue(out var plan) && plan.Cold);

    internal static int ShelterCoolers(ColonyProjection facts) => PolicyFunctions.ShelterCoolers(facts.LayoutPlan.TryGetValue(out var plan) && plan.Hot);

    internal static IReadOnlyList<InteriorRoom> PlannedShelterRooms(ColonyProjection facts)
    {
        var known = facts.LayoutPlan.TryGetValue(out var plan);
        var rooms = PlannedInteriorRooms(facts, planned => planned.Role == PlannedRole.Shelter).ToList();
        for (var i = 0; i < rooms.Count; i++)
        {
            rooms[i] = rooms[i] with { Campfires = PolicyFunctions.ShelterCampfires(known && plan!.Cold), Coolers = PolicyFunctions.ShelterCoolers(known && plan!.Hot) };
        }

        return rooms;
    }

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

            var (room, ok) = PolicyFunctions.InteriorRoomFromLayout(planned, facts.Shapes);
            if (!ok)
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

    internal static IReadOnlyList<InteriorRoom> ResearchHostRooms(ColonyProjection facts) =>
        PolicyFunctions.CapabilitiesOf(PlannedRole.Shelter).Contains(Capability.Research) ? PlannedShelterRooms(facts) : [];

    internal static IReadOnlyList<RoleMismatch> RoleMismatches(ColonyProjection facts)
    {
        var (plan, rooms, known) = PlannedLayout(facts);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var construction);
        var sk = facts.Facts.Sleeping.TryGetValue(out var sleeping);
        if (!known || !ck || !construction!.Colony || !sk || facts.Stations.Stations.Count == 0)
        {
            return [];
        }

        var contents = PolicyFunctions.ObservedRoomContents(facts.Stations, plan, rooms, construction.Buildings, sleeping!);
        return PolicyFunctions.GuardNativeRoles(facts.Stations, plan, rooms, contents);
    }

    internal static void RecordRoleMismatches(CancellationToken ct, ColonyProjection facts)
    {
        IReadOnlyList<RoleMismatch> mismatches;
        try
        {
            mismatches = RoleMismatches(facts);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Tel.Decide(ct, new Decision
            {
                Kind = "layout_role_guard",
                Component = "layout",
                Level = DecisionLevel.Warn,
                Verdict = "failed",
                Reason = "guard_error",
                Target = "rooms",
                Attrs = { ["error"] = e },
            });
            return;
        }

        foreach (var m in mismatches)
        {
            var scores = m.Scores.ToDictionary(kv => kv.Key.Value, kv => kv.Value);
            Tel.Decide(ct, new Decision
            {
                Kind = "layout_role_guard",
                Component = "layout",
                Verdict = "mismatch",
                Reason = PolicyFunctions.RoleMismatchReason,
                Target = m.Room.ToString(),
                Attrs = { ["native"] = m.Native.Value, ["predicted"] = m.Predicted.Value, ["scores"] = scores },
            });
        }
    }
}
