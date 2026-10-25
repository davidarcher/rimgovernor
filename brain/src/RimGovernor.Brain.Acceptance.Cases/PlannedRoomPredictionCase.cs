using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>observations/planned-room-prediction</c> (#3339): the construction activity read's room prediction for a planned
/// room before its ring is built. The game's room role workers and impressiveness over a room that does not yet exist
/// are game mechanics a unit test cannot establish.
/// </summary>
internal static class PlannedRoomPredictionCase
{
    private const string Stuff = "WoodLog";
    private const string Impressiveness = "Impressiveness";
    private const string Cleanliness = "Cleanliness";

    // A cleanliness-positive floor (Cleanliness 0.6) the floored prediction lays on every interior cell (#3797).
    private const string Floor = "SterileTile";

    // A 7x7 clear, unroofed site with a wooden bed at (3,2): the planned room is the 3x3 interior (2..4, 2..4) closed by
    // the ring of the 5x5 rect at (1,1), its door at (3,1).
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 7,
            Height = 7,
            OffsetX = 6,
            Radius = 15,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Heavy],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 7, 7), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
        Buildings = [new Fx.StageBuilding { Name = "bed", Def = "Bed", Stuff = Stuff, Cells = [new(3, 2)] }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "observations/planned-room-prediction",
        Scope = "A wooden bed in an open 3x3 patch: the construction activity read's room prediction, asked for that patch closed by a wood "
            + "wall ring with a door, predicts the role, impressiveness and cleanliness (within 1e-3), cell and open-roof counts the game's own "
            + "room reports through observations_list_rooms once test/stage builds that ring, and lists the bed; asked again with sterile tile "
            + "floor pieces on the interior, it predicts the same impressiveness and the cleanliness the built room reports once test/stage lays "
            + "that floor. Does not prove Rounds composition, the bed thought or selection.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Stage = Spec,
        Ops = [FixtureStage.Tool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("test/stage replied no site");
        var bed = staged.Buildings["bed"].Ids.Single();
        Cell At(int x, int z) => new(site.X + x, site.Z + z);
        var door = At(3, 1);
        var interior = (from z in Enumerable.Range(2, 3) from x in Enumerable.Range(2, 3) select At(x, z)).ToList();
        var ring = (from z in Enumerable.Range(1, 5) from x in Enumerable.Range(1, 5) where x is 1 or 5 || z is 1 or 5 select At(x, z)).ToList();
        var pieces = ring.Select(c => new Observations.PlannedRoomPiece(c, c == door ? "Door" : "Wall", Stuff)).ToList();
        var floored = pieces.Concat(interior.Select(c => new Observations.PlannedRoomPiece(c, Floor, "")));

        var read = await s.CallAsync(
            "predict",
            NativeOps.ReadConstructionActivity,
            new Observations.ConstructionActivityRequest(s.Identity, default)
            {
                RoomPredictions =
                [
                    new("1", Stuff, EquatableArray.From(interior), EquatableArray.From(pieces)),
                    new("2", Stuff, EquatableArray.From(interior), EquatableArray.From(floored)),
                ],
            },
            cancellationToken).ConfigureAwait(false);
        if (read.RoomPredictions is not [var prediction, var floor])
        {
            throw new AcceptanceException($"predict: want two room predictions, got {read.RoomPredictions.Count}; findings {string.Join(",", read.Findings)}");
        }

        // The ring the prediction was asked for, built: the same 5x5 rect in absolute cells, unroofed.
        _ = await s.StageAsync(
            "ring",
            new Fx.StageSpec { Paused = true, Rooms = [new Fx.StageRoom { Name = "ring", Rect = new Fx.StageRect(site.X + 1, site.Z + 1, 5, 5), Stuff = Stuff, Doors = [new(door.X, door.Z)] }] },
            cancellationToken).ConfigureAwait(false);

        var corner = new RimGovernor.Brain.Domain.Cell { X = interior[0].X, Z = interior[0].Z };
        async Task<Observations.RoomState> RoomAsync(string step)
        {
            var rooms = await s.CallAsync(
                step,
                NativeOps.ListRooms,
                new Observations.ListRoomsRequest { Scope = s.Scope, Region = new Observations.Rectangle { Minimum = corner, Maximum = corner } },
                cancellationToken).ConfigureAwait(false);
            var observed = rooms;
            return observed.Rooms is [var one] ? one : throw new AcceptanceException($"{step}: want the one built room at {corner.X},{corner.Z}, got {observed.Rooms.Count}");
        }
        var room = await RoomAsync("room").ConfigureAwait(false);

        // The same room with the planned floor laid on its interior.
        _ = await s.StageAsync(
            "floor",
            new Fx.StageSpec { Paused = true, Cells = [new Fx.StageCells { Rect = new Fx.StageRect(interior[0].X, interior[0].Z, 3, 3), Terrain = Floor }] },
            cancellationToken).ConfigureAwait(false);
        var tiled = await RoomAsync("floored").ConfigureAwait(false);

        var actual = room.Stats.FirstOrDefault(st => st.DefName == Impressiveness)?.Value;
        var problems = new List<string>();
        void Near(string what, double predicted, Observations.RoomState built, string stat)
        {
            if (built.Stats.FirstOrDefault(st => st.DefName == stat)?.Value is not double value || Math.Abs(predicted - value) > 1e-3)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{what} {predicted}, room {built.Stats.FirstOrDefault(st => st.DefName == stat)?.Value}"));
            }
        }
        Near("cleanliness", prediction.Cleanliness, room, Cleanliness);
        Near("floored cleanliness", floor.Cleanliness, tiled, Cleanliness);
        if (!floor.Impressiveness.Equals(prediction.Impressiveness))
        {
            problems.Add($"floored impressiveness {floor.Impressiveness}, unfloored {prediction.Impressiveness}");
        }
        if (prediction.Role != Wire.Name(room.Role))
        {
            problems.Add($"role {prediction.Role}, room {room.Role}");
        }

        if (actual is not double score || Math.Abs(prediction.Impressiveness - score) > 1e-3)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"impressiveness {prediction.Impressiveness}, room {actual}"));
        }

        if (prediction.Cells != room.CellCount)
        {
            problems.Add($"cells {prediction.Cells}, room {room.CellCount}");
        }

        if (prediction.OpenRoofCells != room.OpenRoofCount)
        {
            problems.Add($"open roof {prediction.OpenRoofCells}, room {room.OpenRoofCount}");
        }

        if (!prediction.Beds.Contains(bed) || !room.Beds.Any(b => b.Id == bed))
        {
            problems.Add($"bed {bed}: predicted [{string.Join(",", prediction.Beds)}], room [{string.Join(",", room.Beds.Select(b => b.Id))}]");
        }

        s.RecordWire("prediction", prediction);
        s.RecordWire("floored", floor);
        s.RecordWire("room", room);
        s.RecordWire("tiled", tiled);
        if (problems.Count > 0)
        {
            throw new AcceptanceException("prediction disagrees with the built room: " + string.Join("; ", problems));
        }
    }
}
