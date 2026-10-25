using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry;
using Decision = RimGovernor.Brain.Telemetry.Decision;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>layout/first-bedroom-closes</c> (#3752, epic #3738 D11): a new temperate colony of three with 600 wood beside it,
/// served by the work, shelter, sleeping and clearance families. Its first sleeping room (a Bedroom or Barracks of the
/// plan) stands enclosed and roofed and every colonist owns a bed inside it within the staged cap, with one ring open
/// at a time and no started room re-sited. In order: a layout_plan row records the plan; the first planned sleeping
/// room closes (a proper roofed census room filling exactly its region; the tick is recorded); before that tick no
/// other planned room has a wall or door of its ring built or framed (a cell it shares with the sleeping room's ring is
/// the sleeping room's); every colonist's owned bed is in that census room, whose game role is Bedroom or Barracks; no
/// layout_replan_tiled row written after a room's ring started (a blueprint, frame or built wall or door on it) names
/// that room <c>tile_sited_again</c>; no planner_step row holds a step on no_space/<c>bedroom_template</c>. The open
/// ring order is the portfolio's pricing (#3744, #3754), ring before furniture is #3743 and the started-room pin is
/// #3741; their policy is unit-tested, and this case owns the native closure.
/// </summary>
internal static class LayoutFirstBedroomCloses
{
    private const int Colonists = 3;
    private static readonly HashSet<Observations.RoomRole> SleepingRoles = [Observations.RoomRole.Bedroom, Observations.RoomRole.Barracks];
    private static readonly TelemetryKind[] Read = [TelemetryKinds.LayoutPlan, TelemetryKinds.LayoutReplanTiled, TelemetryKinds.PlannerStep];

    internal static TypedCase Case() => new()
    {
        Name = "layout/first-bedroom-closes",
        Scope = "Issue #3752: a served new temperate colony of three (600 wood beside it, no room standing, asserted) under the "
            + "work, shelter, sleeping and clearance families records a plan, closes and roofs its first planned Bedroom or "
            + "Barracks with every colonist owning a bed inside it, opens no other room's ring (a wall or door built or framed) "
            + "before it closes, re-sites no started room (tile_sited_again) and never holds a bedroom on no_space/bedroom_template.",
        Ticks = TypedCase.StagedTickCap, // the budget the issue settles: 30k ticks with the staged wood
        Tier = CaseTier.Nightly,
        Start = new LabStart { Colonists = Colonists },
        Crew = new Crew { Size = Colonists },
        Keep = ["Food"],
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            World = new Fx.StageWorld { Hour = 7 },
            Things = [new Fx.StageThing { Name = "wood", Def = "WoodLog", Count = 600, Cell = new(1, 1), Near = true }],
        },
        Serve = new ServeSpec { Families = ["work", "shelter", "sleeping", "clearance"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "first-bedroom-closes" },
        Run = RunAsync,
    };

    private static string Text(IReadOnlyDictionary<string, object?> fields, string key) => fields.GetValueOrDefault(key) as string ?? "";

    private static int Number(object? value) => value switch
    {
        double d => (int)d,
        long l => (int)l,
        int i => i,
        _ => -1,
    };

    private static Cell CellOf(RimGovernor.Brain.Domain.Cell c) => new(c.X, c.Z);

    // The cells a planned room's ring stands on; an unfenced room has none.
    private static HashSet<Cell> Ring(PlannedRoom r) => r.Unfenced() ? [] : [.. r.Region.Walls().Cells()];

    // The census room the planned room's ring encloses: proper, indoors, roofed, off the map edge, filling exactly its region.
    private static Observations.RoomState? Enclosing(PlannedRoom r, IEnumerable<Observations.RoomState> rooms)
    {
        var bounds = r.Region.Bounds();
        return rooms.FirstOrDefault(room => room is { ProperRoom: true, Outdoors: false, OpenRoofCount: 0, TouchesMapEdge: false }
            && room.CellCount == (uint)r.Region.Area()
            && room.Extents is { Minimum: var min, Maximum: var max }
            && min.X >= bounds.X && min.Z >= bounds.Z && max.X < bounds.X + bounds.Width && max.Z < bounds.Z + bounds.Height
            && r.Region.Contains(min) && r.Region.Contains(max));
    }

    private sealed record Closed(int Room, PlannedRole Role, long Tick, HashSet<Cell> Ring);

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        if (await FirstRoomReads.RoofedRoomsAsync(s, "rooms-before", cancellationToken).ConfigureAwait(false) is { Count: > 0 } before)
        {
            throw new AcceptanceException($"the lab already holds roofed proper rooms ({string.Join(' ', before.Select(r => r.Id))}): the precondition hides the first room");
        }
        var floor = Read.SelectMany(s.Telemetry).Select(r => r.Sequence).DefaultIfEmpty(0UL).Max();
        LogRow? planned = null;
        Closed? closed = null;
        Observations.RoomState? census = null;
        var owned = new Dictionary<string, string>(StringComparer.Ordinal);

        // Each planned room's ring start: the tick it was first seen and the newest replan row then.
        var started = new Dictionary<int, (long Tick, ulong Replans)>();

        // Each built or framed ring piece by room and cell, with the tick it was first seen.
        var shell = new Dictionary<(int Room, Cell Cell), long>();
        _ = await s.ServeUntilAsync("first-bedroom", s.TicksLeft, async ct =>
        {
            planned ??= s.Telemetry(TelemetryKinds.LayoutPlan).FirstOrDefault(r => r.Sequence > floor
                && Text(r.Fields(), Decision.VerdictKey) == "planned" && Text(r.Fields(), Decision.TargetKey) == "plan");
            if (planned is null || await s.LayoutPlanAsync(ct).ConfigureAwait(false) is not { } plan)
            {
                return ("no plan", false);
            }
            var reply = await s.CallAsync("rooms", NativeOps.ListRooms, new Observations.ListRoomsRequest { Scope = s.Scope }, ct).ConfigureAwait(false);
            var snapshot = reply;
            var tick = snapshot.Context?.Tick ?? throw new AcceptanceException("the rooms read carries no tick");
            var buildings = await s.BuildingsAsync("buildings", new Observations.ListBuildingsRequest(), ct).ConfigureAwait(false);
            foreach (var r in plan.Rooms)
            {
                var ring = Ring(r);
                var (wall, door) = r.RingDefs();
                var pieces = buildings.Where(b => b.Building.Place is Observations.EntityPlace.OnMap { Cell: var at } && ring.Contains(CellOf(at))
                    && (b.Construction is Observations.ConstructionState.Build build ? build.Definition : b.Building.DefName) is { } def && (def == wall || def == door)).ToList();
                if (pieces.Count > 0 && !started.ContainsKey(r.Id))
                {
                    started[r.Id] = (tick, s.Telemetry(TelemetryKinds.LayoutReplanTiled).Select(row => row.Sequence).DefaultIfEmpty(floor).Max());
                }
                foreach (var b in pieces.Where(b => b.Status is Observations.BuildingStatus.Frame or Observations.BuildingStatus.Built))
                {
                    _ = shell.TryAdd((r.Id, CellOf(((Observations.EntityPlace.OnMap)b.Building.Place).Cell)), tick);
                }
            }

            if (closed is null && plan.Rooms.Where(r => r.Role is PlannedRole.Bedroom or PlannedRole.Barracks)
                .Select(r => (Room: r, Census: Enclosing(r, snapshot.Rooms))).FirstOrDefault(m => m.Census is not null) is { Room: { } first })
            {
                closed = new Closed(first.Id, first.Role, tick, Ring(first));
            }
            if (closed is null)
            {
                return (string.Create(CultureInfo.InvariantCulture, $"open={string.Join(',', started.Keys.Order())}"), false);
            }

            // The closed room as the plan now holds it (a started room keeps its id) and the census room it encloses.
            census = plan.Rooms.FirstOrDefault(r => r.Id == closed.Room) is { } room ? Enclosing(room, snapshot.Rooms) : null;
            var pawns = await s.CallAsync("colonists", NativeOps.ListPawns, new Observations.ListPawnsRequest
            {
                Scope = s.Scope,
                Filter = new Observations.PawnFilter { Colonist = RimGovernor.Contracts.Wire.Observations.PawnMembership.Only },
            }, ct).ConfigureAwait(false);
            owned = pawns.Pawns
                .ToDictionary(p => p.Pawn.Id, p => p.OwnedBed?.Id ?? "", StringComparer.Ordinal);
            var beds = census?.Beds.Select(b => b.Id).ToHashSet(StringComparer.Ordinal) ?? [];
            var housed = owned.Count >= Colonists && owned.Values.All(beds.Contains);
            return (string.Create(CultureInfo.InvariantCulture, $"closed={closed.Room} census={census?.Id} housed={owned.Values.Count(beds.Contains)}/{owned.Count}"), housed);
        }, cancellationToken).ConfigureAwait(false);

        // 1. A layout_plan row records the plan.
        var plan = planned ?? throw new AcceptanceException("no layout_plan row recorded the first plan");
        s.Record("first-plan", Text(plan.Fields(), "summary"));
        s.Record("ring-starts", string.Join(' ', started.OrderBy(kv => kv.Value.Tick).Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key}@{kv.Value.Tick}"))));

        // 2. The first planned sleeping room closes, roofed; its tick is recorded.
        var first = closed ?? throw new AcceptanceException("no planned Bedroom or Barracks stood enclosed and roofed within the budget");
        s.Record("closed", string.Create(CultureInfo.InvariantCulture, $"room{first.Room}:{first.Role}@{first.Tick} census={census?.Id}:{census?.Role}"));

        // 3. One open ring: before the close, no other planned room had a ring wall or door built or framed.
        var early = shell.Where(kv => kv.Key.Room != first.Room && kv.Value < first.Tick && !first.Ring.Contains(kv.Key.Cell)).ToList();
        if (early.Count > 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture,
                $"a second ring opened before room {first.Room} closed at {first.Tick}: {string.Join(' ', early.Select(kv => $"room{kv.Key.Room}@{kv.Key.Cell.X},{kv.Key.Cell.Z}:{kv.Value}"))}"));
        }

        // 4. Every colonist owns a bed inside it, and the game calls it a Bedroom or Barracks.
        s.Record("owned-beds", string.Join(' ', owned.Select(kv => $"{kv.Key}={kv.Value}")));
        var inside = census?.Beds.Select(b => b.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        if (census is null || owned.Count < Colonists || !owned.Values.All(inside.Contains))
        {
            throw new AcceptanceException($"not every colonist owns a bed in room {first.Room} (census {census?.Id ?? "none"}, beds {string.Join(',', inside)}): {string.Join(' ', owned.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }
        if (!SleepingRoles.Contains(census.Role))
        {
            throw new AcceptanceException($"the game's role for room {first.Room} (census {census.Id}) is {census.Role}, not Bedroom or Barracks");
        }

        // 5. No started room re-sited: no replan row after a room's ring start names it tile_sited_again.
        var resited = new List<string>();
        foreach (var row in s.Telemetry(TelemetryKinds.LayoutReplanTiled).Where(r => r.Sequence > floor))
        {
            foreach (var entry in (row.Fields().GetValueOrDefault("rooms") as IEnumerable<object?> ?? []).OfType<IReadOnlyDictionary<string, object?>>())
            {
                var id = Number(entry.GetValueOrDefault("room"));
                if (Text(entry, "reason") == LayoutReplanTiledRules.ReasonDiffSited && started.TryGetValue(id, out var start) && row.Sequence > start.Replans)
                {
                    resited.Add(string.Create(CultureInfo.InvariantCulture, $"room{id} (ring started {start.Tick}) in replan seq {row.Sequence} as {Text(entry, "id")}"));
                }
            }
        }
        if (resited.Count > 0)
        {
            throw new AcceptanceException($"a started room was sited again: {string.Join("; ", resited)}");
        }

        // 6. No bedroom step held on no_space/bedroom_template (the #3743 deadlock).
        var held = s.Telemetry(TelemetryKinds.PlannerStep).Where(r => r.Sequence > floor
            && Text(r.Fields(), Decision.ReasonKey) == Cause.NoSpace.Value && Text(r.Fields(), "subject") == SleepingRules.BedroomTemplateSubject).ToList();
        if (held.Count > 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture,
                $"{held.Count} planner_step rows held a bedroom on {Cause.NoSpace.Value}/{SleepingRules.BedroomTemplateSubject}, first {Text(held[0].Fields(), Decision.TargetKey)}"));
        }
    }
}
