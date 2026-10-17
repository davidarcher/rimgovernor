using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

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

        async Task<List<Observations.RoomState>> ReadAsync(string label, Observations.ListRoomsRequest request)
        {
            var reply = await s.CallAsync(label, NativeOps.ListRooms, request with { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
            var rooms = TypedReads.Observed(label, NativeOps.ListRooms, reply, reply.Outcome?.Observed).Rooms.ToList();
            s.RecordWire(label, rooms);
            return rooms;
        }

        var cell = new Common.Cell { X = inside.X, Z = inside.Z };
        var byCell = await ReadAsync("by-cell", new Observations.ListRoomsRequest { Region = new Observations.Rectangle { Minimum = cell, Maximum = cell } }).ConfigureAwait(false);
        if (byCell is not [{ Id: { } id } room])
        {
            throw new AcceptanceException($"by-cell: want one room at {inside}, got {byCell.Count}");
        }

        if (room is not { ProperRoom: true, Outdoors: false, Doorway: false, Snapshot: not null })
        {
            problems.Add($"hut: want a proper indoor room with a snapshot, got {room}");
        }

        if (room.CellCount != (uint)interior.Count)
        {
            problems.Add($"hut: cellCount {room.CellCount}, want the staged interior's {interior.Count}");
        }

        var (minX, minZ, maxX, maxZ) = (interior.Min(c => c.X), interior.Min(c => c.Z), interior.Max(c => c.X), interior.Max(c => c.Z));
        if (room.Extents is not { Minimum: { } min, Maximum: { } max } || min.X != minX || min.Z != minZ || max.X != maxX || max.Z != maxZ)
        {
            problems.Add($"hut: extents {room.Extents}, want ({minX},{minZ})-({maxX},{maxZ})");
        }

        if (room.Center is not { } center || !interior.Contains(new Fx.StageCell(center.X, center.Z)))
        {
            problems.Add($"hut: center {room.Center} is not an interior cell");
        }

        var exact = new Observations.ListRoomsRequest { RoomIds = [id] };
        var byId = await ReadAsync("by-id", exact).ConfigureAwait(false);
        if (byId is not [var same] || same != room)
        {
            problems.Add("by-id: the exact-id read differs from the cell read");
        }

        if (!(await ReadAsync("repeat", exact).ConfigureAwait(false)).SequenceEqual(byId))
        {
            problems.Add("repeat: the exact-id read changed");
        }

        if ((await ReadAsync("unknown-id", new Observations.ListRoomsRequest { RoomIds = ["absent-room"] }).ConfigureAwait(false)).Count != 0)
        {
            problems.Add("unknown-id: an absent room id matched a room");
        }

        var boundary = await ReadAsync("boundary", exact with { IncludeBoundary = true }).ConfigureAwait(false);
        static long Units(Observations.RoomState r) => r.Contents.Sum(q => q.Units ?? 0);
        if (boundary is not [var walled] || Units(walled) <= Units(room))
        {
            problems.Add($"boundary: contents {(boundary.Count == 1 ? Units(boundary[0]) : -1)} do not exceed the interior's {Units(room)}");
        }

        var duplicate = await s.CallAsync("duplicate-id", NativeOps.ListRooms, new Observations.ListRoomsRequest { Scope = s.Scope, RoomIds = [id, id] }, cancellationToken).ConfigureAwait(false);
        if (duplicate.Outcome?.Failure?.Code != Common.FailureCode.InvalidRequest)
        {
            problems.Add($"duplicate-id: want INVALID_REQUEST, got {duplicate.Outcome}");
        }

        if (problems.Count > 0)
        {
            throw new AcceptanceException(string.Join("; ", problems));
        }
    }
}
