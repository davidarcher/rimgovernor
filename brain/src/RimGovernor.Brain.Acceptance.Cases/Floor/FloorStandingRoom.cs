using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using DomainWire = RimGovernor.Brain.Domain.Wire;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>floor/standing-room-reconcile</c> (Go <c>floor/standing_room.go</c>): a planned room that already stands is reconciled
/// in place and its enclosure is kept. The plan's first bedroom stands finished: wooden walls, a door, a roof and a
/// Concrete floor (beauty -1, which the Living tier fails). Granite blocks are in a stockpile and Stonecutting is known,
/// so the reconciler's per-cell diff has two jobs: the floor_out/floor_in of the Concrete, and the wall_up swap of the
/// wooden walls for stone. The case runs the controller in bursts and audits the ground between them. At every audit
/// the room must still be sealed (a wall mid-swap stands as a frame) and none of the staged walls or doors may be a
/// deconstruction target: a standing room is never torn down to be rebuilt. It passes when every ring wall is stone and
/// no interior cell is Concrete.
/// </summary>
/// <remarks>
/// Parity notes against the Go case. The controller runs as one in-mod service for all bursts (an inert one first, only
/// to record the layout plan) rather than a restarted process per burst; the bursts are 5000-tick windows of its game
/// time with a native audit at the end of each. Go read the staged cells' deconstruction targets from the journal's plan
/// history; the player API exposes only the active plan, so the tear-down check samples it and fails on a
/// deconstruction action over a staged wall or door cell. The layout plan comes from the unwired
/// <see cref="ObservedSession.LayoutPlanReader"/> seam (it fails clearly until it is wired).
/// </remarks>
internal static class FloorStandingRoom
{
    private const int Bursts = 16;
    private const int BurstTicks = 5000;
    private const string Stone = "BlocksGranite";
    private const string Prefix = "standing-room";

    internal static AcceptanceCase Case() => new()
    {
        Name = "floor/standing-room-reconcile",
        Scope = "A standing planned bedroom with wooden walls and a Concrete floor, stone blocks in stock: the reconciler lays a "
            + "fitting floor and swaps the walls to stone in place while the room stays enclosed and no staged wall or door "
            + "is ever deconstructed (#2118). Native: floor and wall jobs, vanilla's in-place frame, the enclosure read.",
        Start = new LabStart(),
        RequiredOps = [PlanStage.Tool, FixtureStage.Tool],
        Service = true,
        Budget = TimeSpan.FromMinutes(14),
        Crew = new Crew { Size = 3 },
        Reason = "an inert service run reads the plan the room is staged from, then up to sixteen controller bursts with an audit "
            + "between each; the floor and the walls are real pawn work over game time",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        RoutineService? run = null;
        try
        {
            await BodyAsync(s, value => run = value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (run is not null)
            {
                await RoutineService.PostmortemAsync(s, run, (_, _) => Task.CompletedTask).ConfigureAwait(false);
            }
        }
    }

    private static async Task BodyAsync(ISession s, Action<RoutineService?> held, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var centre = s.Prepared?["center"].AsMap() ?? [];
        var middle = new StageCell((int)centre["x"].AsNumber(), (int)centre["z"].AsNumber());
        if (middle == default)
        {
            throw new AcceptanceException($"lab start: no centre in {s.Prepared.ToJson()}");
        }

        _ = await FixtureStage.ApplyAsync(h, "stage-research", new Fx.StageSpec { Research = [new() { Def = "Stonecutting" }, new() { Def = "ComplexFurniture" }] }, cancellationToken).ConfigureAwait(false);

        // An inert run (the work family only) so the controller records the layout plan the room is staged from.
        var one = await RoutineService.StartAsync(s, Prefix, ["work"], cancellationToken).ConfigureAwait(false);
        held(one);
        var layout = await RoutineService.BoundedAsync(
            async ct =>
            {
                while (true)
                {
                    var (_, plan) = await ObservedSession.ReadLayoutPlanAsync(s.Config, ct).ConfigureAwait(false);
                    if (plan is not null && plan.Rooms.Any(r => r.Role == PolicyFunctions.PlannedBedroom))
                    {
                        return plan;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                }
            },
            TimeSpan.FromMinutes(5),
            "layout plan with a bedroom",
            cancellationToken).ConfigureAwait(false);
        h = await one.ReleaseAsync(s, cancellationToken).ConfigureAwait(false);
        held(null);

        var geometry = layout.Rooms.Select(room => Geometry(layout, room)).ToList();
        var bedroom = PlanStage.FirstRoom(geometry, PolicyFunctions.PlannedBedroom.ToString())
            ?? throw new AcceptanceException("the layout plan holds no bedroom");
        var ring = PlanStage.RingOf(bedroom);
        var pile = PlanStage.FreeRect(geometry, middle, 6, 4)
            ?? throw new AcceptanceException("no free ground for the stockpile in the layout plan");
        var spec = ring.Spec("WoodLog", "Concrete") with
        {
            Drops = [new StageDrop(Stone, 400, pile.MinX, pile.MinZ), new StageDrop("WoodLog", 200, pile.MinX + 1, pile.MinZ)],
            Stockpile = pile,
        };
        var staged = await PlanStage.StageAsync(h, "stage-room", spec, cancellationToken).ConfigureAwait(false);
        var before = await PlanStage.ReadAsync(h, "audit-before", [], ring.Cells(), cancellationToken).ConfigureAwait(false);
        try
        {
            ring.Sealed(before);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("the staged bedroom is not a standing room: " + e.Message, e);
        }

        report["staged"] = new JsonObject
        {
            ["interior"] = string.Create(CultureInfo.InvariantCulture, $"{bedroom.X},{bedroom.Z} {bedroom.Width}x{bedroom.Height}"),
            ["walls"] = staged.Walls.Count,
            ["doors"] = staged.Doors.Count,
            ["stockpile"] = string.Create(CultureInfo.InvariantCulture, $"{pile.MinX},{pile.MinZ}..{pile.MaxX},{pile.MaxZ}"),
        };

        var protectedCells = new HashSet<(int X, int Z)>(ring.Walls.Select(c => (c.X, c.Z)).Concat(ring.Doors.Select(d => (d.X, d.Z))));
        var bursts = new JsonArray();
        report["bursts"] = bursts;
        var run = await RoutineService.StartAsync(s, Prefix, ["sleeping", "clearance", "work", "flooring"], cancellationToken).ConfigureAwait(false);
        held(run);
        for (var burst = 1; burst <= Bursts; burst++)
        {
            string? torn = null;
            var first = await run.GameTickAsync(cancellationToken).ConfigureAwait(false);
            await run.WaitCensusAsync(
                string.Create(CultureInfo.InvariantCulture, $"burst {burst}: {BurstTicks} ticks of game time"),
                TimeSpan.FromMinutes(5),
                async ct =>
                {
                    var now = await run.GameTickAsync(ct).ConfigureAwait(false);
                    torn ??= await TornDownAsync(run, protectedCells, ct).ConfigureAwait(false);
                    var elapsed = (long)now - (long)first;
                    return (elapsed.ToString(CultureInfo.InvariantCulture), elapsed >= BurstTicks || torn is not null);
                },
                cancellationToken).ConfigureAwait(false);
            if (torn is not null)
            {
                throw new AcceptanceException($"burst {burst}: the controller targeted a staged {torn} for deconstruction: a standing room is reconciled in place");
            }

            var audit = await PlanStage.ReadAsync(h, string.Create(CultureInfo.InvariantCulture, $"audit-{burst}"), [], ring.Cells(), cancellationToken).ConfigureAwait(false);
            try
            {
                ring.Sealed(audit);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"burst {burst}: the room lost its enclosure: " + e.Message, e);
            }

            var wood = ring.Walls.Count(c => audit.CellAt(c).EdificeStuff == "WoodLog");
            var concrete = ring.Interior.Count(c => audit.CellAt(c).Terrain == "Concrete");
            bursts.Add(new JsonObject { ["burst"] = burst, ["wood_walls"] = wood, ["concrete_cells"] = concrete });
            if (wood == 0 && concrete == 0)
            {
                report["result"] = string.Create(CultureInfo.InvariantCulture, $"stone walls and no concrete after {burst} bursts");
                _ = await run.ReleaseAsync(s, cancellationToken).ConfigureAwait(false);
                held(null);
                return;
            }
        }

        throw new AcceptanceException($"after {Bursts} bursts of {BurstTicks} ticks the room still has wooden walls or a Concrete floor: {bursts.ToJson()}");
    }

    private static PlannedRoomGeometry Geometry(LayoutPlan plan, PlannedRoom room)
    {
        var interior = room.Region.Bounds();
        return new PlannedRoomGeometry
        {
            Role = room.Role.ToString(),
            X = interior.X,
            Z = interior.Z,
            Width = interior.Width,
            Height = interior.Height,
            Door = new StageCell(room.Door.X, room.Door.Z),
            DoorRotation = room.DoorRot is { } rotation ? PlanStage.RotationNumber(DomainWire.Name(rotation)) : 0,
            ShellDoors = [.. plan.ShellDoors(room).Select(d => new StageCell(d.X, d.Z))],
        };
    }

    // The first staged wall or door the active plan deconstructs, "" for none (null here). The player API holds only the
    // active plan, so this is sampled each poll of a burst rather than read back from a plan history.
    private static async Task<string?> TornDownAsync(RoutineService run, HashSet<(int X, int Z)> staged, CancellationToken cancellationToken)
    {
        var state = await run.Service.GetAsync("GET", "/api/state", cancellationToken).ConfigureAwait(false);
        var id = state["activePlanId"].AsString();
        if (id.Length == 0)
        {
            return null;
        }

        JsonObject plan;
        try
        {
            plan = await run.LoadPlanAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException)
        {
            // The plan retired between the two reads.
            return null;
        }

        var cut = DomainWire.Name(ActionKind.Deconstruction);
        foreach (var raw in plan["actions"].AsSlice())
        {
            var action = raw.AsMap() ?? [];
            if (action["kind"].AsString() != cut)
            {
                continue;
            }

            var b = RoutineService.Building(action);
            if (b is not null && staged.Contains(((int)b["x"].AsNumber(), (int)b["z"].AsNumber())))
            {
                return string.Create(CultureInfo.InvariantCulture, $"{b["defName"].AsString()} at {b["x"].AsNumber()},{b["z"].AsNumber()}");
            }
        }

        return null;
    }
}
