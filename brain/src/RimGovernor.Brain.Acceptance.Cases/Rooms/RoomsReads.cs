using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>rooms/reads</c>: observations_list_rooms over a staged roofed hut. The cell, exact-id and boundary lookups agree,
/// the row's geometry matches the staged interior, and the native room flags and contents are the game's own. A
/// snapshot test cannot see the game's room grouping.
/// </summary>
internal static class RoomsReads
{
    private static readonly Fx.StageRect Hut = new(1, 1, 7, 7);

    internal static TypedCase Case() => new()
    {
        Name = "rooms/reads",
        Scope = "observations_list_rooms over a staged 7x7 roofed wooden hut with a stool and steel inside: a one-cell region "
            + "and the exact id select the same proper indoor room, whose cell count, extents and center match the staged "
            + "interior; the repeat read is equal, an unknown id selects nothing, a duplicate id is INVALID_REQUEST, and "
            + "the boundary read adds the walls and door to its contents. Read-only.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 9,
                Height = 9,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 8,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 9, 9), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Pawns, Fx.StageClearKind.Roofs] }],
            Rooms = [new Fx.StageRoom { Name = "hut", Rect = Hut, Stuff = "WoodLog", Doors = [new(4, 1)], Roof = true }],
            Buildings = [new Fx.StageBuilding { Name = "stool", Def = "Stool", Stuff = "WoodLog", Cells = [new(3, 3)] }],
            Things = [new Fx.StageThing { Name = "steel", Def = "Steel", Count = 40, Cell = new(5, 5) }],
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var interior = s.Staged!.Rooms["hut"].Interior;
        var inside = interior[interior.Count / 2];
        var problems = new List<string>();

        async Task<List<RimGovernor.Brain.GameFacts.Observations.RoomState>> ReadAsync(string label, RimGovernor.Brain.GameOrders.Observations.ListRoomsRequest request)
        {
            var reply = await s.CallAsync(label, NativeOps.ListRooms, request with { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
            var rooms = reply.Rooms.ToList();
            s.RecordWireRows(label, rooms);
            return rooms;
        }

        var cell = new RimGovernor.Brain.Domain.Cell { X = inside.X, Z = inside.Z };
        var byCell = await ReadAsync("by-cell", new RimGovernor.Brain.GameOrders.Observations.ListRoomsRequest { Region = new RimGovernor.Brain.GameFacts.Observations.Rectangle { Minimum = cell, Maximum = cell } }).ConfigureAwait(false);
        if (byCell is not [{ Id: var id } room])
        {
            throw new AcceptanceException($"by-cell: want one room at {inside}, got {byCell.Count}");
        }

        if (room is not { ProperRoom: true, Outdoors: false, Doorway: false })
        {
            problems.Add($"hut: want a proper indoor room, got {room}");
        }

        if (room.CellCount != (uint)interior.Count)
        {
            problems.Add($"hut: cellCount {room.CellCount}, want the staged interior's {interior.Count}");
        }

        var (minX, minZ, maxX, maxZ) = (interior.Min(c => c.X), interior.Min(c => c.Z), interior.Max(c => c.X), interior.Max(c => c.Z));
        var (min, max) = (room.Extents.Minimum, room.Extents.Maximum);
        if (min.X != minX || min.Z != minZ || max.X != maxX || max.Z != maxZ)
        {
            problems.Add($"hut: extents {room.Extents}, want ({minX},{minZ})-({maxX},{maxZ})");
        }

        if (!interior.Contains(new Fx.StageCell(room.Center.X, room.Center.Z)))
        {
            problems.Add($"hut: center {room.Center} is not an interior cell");
        }

        var exact = new RimGovernor.Brain.GameOrders.Observations.ListRoomsRequest { RoomIds = [id] };
        var byId = await ReadAsync("by-id", exact).ConfigureAwait(false);
        if (byId is not [var same] || same != room)
        {
            problems.Add("by-id: the exact-id read differs from the cell read");
        }

        if (!(await ReadAsync("repeat", exact).ConfigureAwait(false)).SequenceEqual(byId))
        {
            problems.Add("repeat: the exact-id read changed");
        }

        if ((await ReadAsync("unknown-id", new RimGovernor.Brain.GameOrders.Observations.ListRoomsRequest { RoomIds = ["absent-room"] }).ConfigureAwait(false)).Count != 0)
        {
            problems.Add("unknown-id: an absent room id matched a room");
        }

        var boundary = await ReadAsync("boundary", exact with { IncludeBoundary = true }).ConfigureAwait(false);
        static long Units(RimGovernor.Brain.GameFacts.Observations.RoomState r) => r.Contents.Sum(q => q.Units);
        if (boundary is not [var walled] || Units(walled) <= Units(room))
        {
            problems.Add($"boundary: contents {(boundary.Count == 1 ? Units(boundary[0]) : -1)} do not exceed the interior's {Units(room)}");
        }

        try
        {
            var duplicate = await s.CallAsync("duplicate-id", NativeOps.ListRooms, new RimGovernor.Brain.GameOrders.Observations.ListRoomsRequest { Scope = s.Scope, RoomIds = [id, id] }, cancellationToken).ConfigureAwait(false);
            problems.Add($"duplicate-id: want a refusal, got {duplicate.Rooms.Count} rooms");
        }
        catch (NativeRefusedException refused)
        {
            // A duplicated room id is an invalid request: the native refuses the read.
            s.Record("duplicate-id", refused.Cause);
        }

        if (problems.Count > 0)
        {
            throw new AcceptanceException(string.Join("; ", problems));
        }
    }
}
