using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>clearance/pack-reinstall</c> (Go <c>pack_reinstall.go</c>): a standing furnished room is reconciled to a different
/// plan and its packable piece makes the round trip. The plan's starter shelter stands finished with one Bed in it
/// (quality Excellent, 60 percent hit points) and the plan's first bedroom stands finished, roofed and empty. The
/// sleeping planner reconciles the bedroom (BedroomReconcile): the shelter's vacant bed is packed (uninstall_building,
/// vanilla minifies it and hauls it to the stockpile) and the bedroom's bed is installed from that packed stock
/// (move_building) in its template cell rather than built on site. The case asserts the same building came out the
/// other end: its load id is the one staged, spawned and unpacked in the bedroom, its quality and hit points unchanged.
/// Which cell the piece lands on, and the planner decisions behind the reconcile, are snapshot tests (policy.Reconcile,
/// BedroomTemplate). Hauling to the warehouse is vanilla's own work after the uninstall and is not asserted.
/// </summary>
internal static class ClearancePackReinstall
{
    private const int PackQuality = 4; // Excellent
    private const double PackHitFrac = 0.6; // damaged: hit points must survive the round trip
    private const int PackWood = 300; // floors and the bedroom's own work

    private static readonly string[] AllMethods = ["*"];

    internal static AcceptanceCase Case() => new()
    {
        Name = "clearance/pack-reinstall",
        Scope = "A standing starter shelter holding a Bed and an empty standing planned bedroom: the sleeping planner packs the "
            + "shelter bed and installs the same building, quality and hit points kept, in the bedroom from packed stock (#2118). "
            + "Native: uninstall and install jobs with their reservations, the ReadPackedItems read contract, vanilla minifying.",
        Start = new LabStart(),
        RequiredOps = [PlanStage.Tool, FixtureStage.Tool],
        Serve = new ServeSpec { Families = ["sleeping", "clearance", "work"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "pack-reinstall" },
        Budget = TimeSpan.FromMinutes(14),
        Crew = new Crew { Size = 3 },
        Reason = "two service runs: an inert one reads the layout plan the rooms are staged from, then the controller reconciles; "
            + "the pack, haul and install are real pawn work over game time",
        Run = RunAsync,
    };

    private static PlannedRoomGeometry Geometry(LayoutPlan plan, PlannedRoom room)
    {
        var bounds = room.Region.Bounds();
        return new PlannedRoomGeometry
        {
            Role = room.Role.ToString(),
            X = bounds.X,
            Z = bounds.Z,
            Width = bounds.Width,
            Height = bounds.Height,
            Door = new StageCell(room.Door.X, room.Door.Z),
            DoorRotation = room.DoorRot is { } rot ? PlanStage.RotationNumber(rot.ToString()) : 0,
            ShellDoors = [.. plan.ShellDoors(room).Select(c => new StageCell(c.X, c.Z))],
        };
    }

    private static JsonObject RectJson(Rectangle r) => new() { ["x"] = r.X, ["z"] = r.Z, ["width"] = r.Width, ["height"] = r.Height };

    private static bool Inside(Rectangle r, int x, int z) => x >= r.X && x < r.X + r.Width && z >= r.Z && z < r.Z + r.Height;

    /// <summary>Adds b's walls, doors, roofs and floors to a; a wall or door cell both rings share is staged once.</summary>
    private static StageSpec Merge(StageSpec a, StageSpec b)
    {
        var have = new HashSet<StageCell>();
        foreach (var w in a.Walls)
        {
            _ = have.Add(new StageCell(w.X, w.Z));
        }

        foreach (var d in a.Doors)
        {
            _ = have.Add(new StageCell(d.X, d.Z));
        }

        return a with
        {
            Walls = [.. a.Walls, .. b.Walls.Where(w => !have.Contains(new StageCell(w.X, w.Z)))],
            Doors = [.. a.Doors, .. b.Doors.Where(d => !have.Contains(new StageCell(d.X, d.Z)))],
            Roof = [.. a.Roof, .. b.Roof],
            Floor = [.. a.Floor, .. b.Floor],
        };
    }

    private static Wait PhaseWait(ServiceProcess service) => new()
    {
        Ceiling = PlanStage.Ceiling,
        Stall = Wait.StallBudget(),
        Interval = TimeSpan.FromSeconds(1),
        Terminal = service.Exited,
    };

    private static async Task<ServiceProcess> StartPhaseAsync(ISession s, IReadOnlyList<string> families, CancellationToken cancellationToken)
    {
        var service = await s.ServeAsync(s.Spec with { Families = families }, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _ = await service.StopAsync().ConfigureAwait(false);
            throw;
        }

        service.KeepAuthority(cancellationToken);
        return service;
    }

    /// <summary>Ends a phase and takes the harness back, paused.</summary>
    private static async Task StopPhaseAsync(ISession s, ServiceProcess service, string label, CancellationToken cancellationToken)
    {
        if (await service.StopAsync().ConfigureAwait(false) is { } keep)
        {
            s.Report[label] = keep;
        }

        await ClearanceKit.ReattachAsync(s, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var center = s.Prepared?["center"].AsMap();
        var middle = new StageCell((int)Num(center?["x"]), (int)Num(center?["z"]));
        if (middle == default)
        {
            throw new AcceptanceException($"lab start: no centre in {s.Prepared.ToJson()}");
        }

        // Wooden beds need ComplexFurniture and the tier reads Masonry with Stonecutting, as the bedroom case's colony does.
        _ = await FixtureStage.ApplyAsync(s.Harness, "stage-research", new Fx.StageSpec { Research = [new() { Def = "Stonecutting" }, new() { Def = "ComplexFurniture" }] }, cancellationToken).ConfigureAwait(false);

        // Phase one: an inert controller derives and records the layout plan.
        var one = await StartPhaseAsync(s, ["work"], cancellationToken).ConfigureAwait(false);
        LayoutPlan? found = null;
        try
        {
            await PhaseWait(one).WaitProgressAsync(
                async ct =>
                {
                    var (tick, plan) = await ObservedSession.ReadLayoutPlanAsync(s.Config, ct).ConfigureAwait(false);
                    if (plan is null)
                    {
                        return ("no-plan", false);
                    }

                    if (!plan.Rooms.Any(r => r.Role == PlannedRole.Shelter) || !plan.Rooms.Any(r => r.Role == PlannedRole.Bedroom))
                    {
                        return (string.Create(CultureInfo.InvariantCulture, $"plan-{tick}"), false);
                    }

                    found = plan;
                    return ("plan", true);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            _ = await one.StopAsync().ConfigureAwait(false);
            throw new AcceptanceException($"layout plan with a shelter and a bedroom: {e.Message}", e);
        }

        await StopPhaseAsync(s, one, "plan_service", cancellationToken).ConfigureAwait(false);
        var layout = found!;
        var shelterRoom = layout.Rooms.First(r => r.Role == PlannedRole.Shelter);
        var bedroomRoom = layout.Rooms.First(r => r.Role == PlannedRole.Bedroom);
        var rooms = layout.Rooms.Select(r => Geometry(layout, r)).ToList();
        var shelter = Geometry(layout, shelterRoom);
        var bedroom = Geometry(layout, bedroomRoom);
        var shelterBounds = shelterRoom.Region.Bounds();
        var bedroomBounds = bedroomRoom.Region.Bounds();

        // Stage both rooms finished, the bed in the shelter, wood in a stockpile.
        var shellRing = PlanStage.RingOf(shelter);
        var bedRing = PlanStage.RingOf(bedroom);
        var pile = PlanStage.FreeRect(rooms, middle, 6, 4)
            ?? throw new AcceptanceException($"no free ground for the stockpile in the layout plan ({rooms.Count} rooms)");
        var spec = Merge(shellRing.Spec("WoodLog", ""), bedRing.Spec("WoodLog", ""));
        var bedRot = shelterBounds.Height < 2 ? 1 : 0;
        spec = spec with
        {
            Things = [new StageBuilding { Def = "Bed", Stuff = "WoodLog", X = shelterBounds.X, Z = shelterBounds.Z, Rotation = bedRot, Quality = PackQuality, HitFraction = PackHitFrac }],
            Drops = [new StageDrop("WoodLog", PackWood, pile.MinX, pile.MinZ)],
            Stockpile = pile,
        };
        var staged = await PlanStage.StageAsync(s.Harness, "stage-rooms", spec, cancellationToken).ConfigureAwait(false);
        if (staged.Things.Count != 1)
        {
            throw new AcceptanceException($"staged {staged.Things.Count} things, want the one bed: {staged.Reply.ToJson()}");
        }

        var bed = staged.Things[0];
        List<StageCell> bedCells = [.. bedRing.Cells(), .. shellRing.Cells()];
        var before = await PlanStage.ReadAsync(s.Harness, "audit-before", [bed], bedCells, cancellationToken).ConfigureAwait(false);
        var start = before.Things.TryGetValue(bed, out var read) ? read : new ThingRead();
        report["staged"] = new JsonObject
        {
            ["shelter"] = RectJson(shelterBounds),
            ["bedroom"] = RectJson(bedroomBounds),
            ["bed"] = ThingJson(start),
            ["stockpile"] = new JsonObject { ["minX"] = pile.MinX, ["minZ"] = pile.MinZ, ["maxX"] = pile.MaxX, ["maxZ"] = pile.MaxZ },
        };
        if (!start.Found || start.Def != "Bed" || start.Quality != PackQuality || start.HitPoints >= start.MaxHP)
        {
            throw new AcceptanceException($"the staged bed is not a damaged Excellent Bed: {ThingJson(start).ToJsonString()}");
        }

        try
        {
            bedRing.Sealed(before);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"the staged bedroom is not a standing room: {e.Message}", e);
        }

        // Phase two: the real families reconcile the bedroom.
        var two = await StartPhaseAsync(s, s.Spec.Families ?? [], cancellationToken).ConfigureAwait(false);
        bool packedDone = false, installedDone = false;
        JsonObject installed = [];
        try
        {
            using var journal = await CaseJournal.OpenAsync(s.Config, cancellationToken).ConfigureAwait(false);
            await PhaseWait(two).WaitProgressAsync(
                async ct =>
                {
                    var plans = await journal.PlanHistoryAsync(256, AllMethods, ct).ConfigureAwait(false);
                    foreach (var (kind, action) in CaseJournal.CompletedActions(plans))
                    {
                        if (action["thing"].AsString() != bed)
                        {
                            continue;
                        }

                        if (kind == Wire.Name(ActionKind.UninstallBuilding))
                        {
                            packedDone = true;
                        }
                        else if (kind == Wire.Name(ActionKind.MoveBuilding))
                        {
                            installedDone = true;
                            installed = action;
                        }
                    }

                    var (tick, _) = await ObservedSession.ReadLayoutPlanAsync(s.Config, ct).ConfigureAwait(false);
                    return (Wait.Signature(packedDone, installedDone, tick / Wait.TicksPerDay), packedDone && installedDone);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            _ = await two.StopAsync().ConfigureAwait(false);
            throw new AcceptanceException($"the shelter bed packed and installed in the bedroom: {e.Message}", e);
        }

        await StopPhaseAsync(s, two, "reconcile_service", cancellationToken).ConfigureAwait(false);
        var installCell = installed["cell"].AsMap() ?? [];
        report["install"] = new JsonObject { ["cell"] = installCell.DeepClone(), ["rotation"] = installed["rotation"]?.DeepClone() };

        // The same building stands in the bedroom: spawned, not packed, quality and hit points kept.
        var cell = new StageCell((int)Num(installCell["x"]), (int)Num(installCell["z"]));
        List<StageCell> afterCells = [cell, .. bedCells];
        var after = await PlanStage.ReadAsync(s.Harness, "audit-after", [bed], afterCells, cancellationToken).ConfigureAwait(false);
        var end = after.Things.TryGetValue(bed, out var endRead) ? endRead : new ThingRead();
        report["after"] = new JsonObject { ["bed"] = ThingJson(end), ["cell"] = after.CellAt(cell).Role };
        if (!end.Found || end.Packed || !end.Spawned || end.Def != "Bed")
        {
            throw new AcceptanceException($"the bed did not come out of the pack installed: {ThingJson(end).ToJsonString()}");
        }

        if (end.Quality != start.Quality || end.HitPoints != start.HitPoints)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the round trip changed the bed: quality {start.Quality} -> {end.Quality}, hit points {start.HitPoints} -> {end.HitPoints}"));
        }

        if (!Inside(bedroomBounds, end.X, end.Z) || Inside(shelterBounds, end.X, end.Z))
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the bed stands at {end.X},{end.Z}, not in the planned bedroom {bedroomBounds} (shelter {shelterBounds})"));
        }

        var role = after.CellAt(new StageCell(end.X, end.Z)).Role;
        if (role != "Bedroom")
        {
            throw new AcceptanceException($"the bed's room reads \"{role}\", not Bedroom");
        }
    }

    private static JsonObject ThingJson(ThingRead t) => new()
    {
        ["id"] = t.Id,
        ["def"] = t.Def,
        ["found"] = t.Found,
        ["packed"] = t.Packed,
        ["spawned"] = t.Spawned,
        ["quality"] = t.Quality,
        ["hitPoints"] = t.HitPoints,
        ["maxHitPoints"] = t.MaxHP,
        ["x"] = t.X,
        ["z"] = t.Z,
    };
}
