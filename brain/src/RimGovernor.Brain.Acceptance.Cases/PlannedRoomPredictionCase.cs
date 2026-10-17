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
            + "wall ring with a door, predicts the role, impressiveness (within 1e-3), cell and open-roof counts the game's own room reports "
            + "through observations_list_rooms once test/stage builds that ring, and lists the bed. Does not prove Rounds composition, the bed "
            + "thought or selection.",
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
        var pieces = ring.Select(c => new Observations.PlannedRoomPiece(c, c == door ? "Door" : "Wall", Stuff));

        var read = await s.CallAsync(
            "predict",
            NativeOps.ReadConstructionActivity,
            new Observations.ConstructionActivityRequest(s.Identity, default)
            {
                RoomPredictions = [new("1", Stuff, EquatableArray.From(interior), EquatableArray.From(pieces))],
            },
            cancellationToken).ConfigureAwait(false);
        if (read.RoomPredictions is not [var prediction])
        {
            throw new AcceptanceException($"predict: want one room prediction, got {read.RoomPredictions.Count}; findings {string.Join(",", read.Findings)}");
        }

        // The ring the prediction was asked for, built: the same 5x5 rect in absolute cells, unroofed.
        _ = await s.StageAsync(
            "ring",
            new Fx.StageSpec { Paused = true, Rooms = [new Fx.StageRoom { Name = "ring", Rect = new Fx.StageRect(site.X + 1, site.Z + 1, 5, 5), Stuff = Stuff, Doors = [new(door.X, door.Z)] }] },
            cancellationToken).ConfigureAwait(false);

        var corner = new Common.Cell { X = interior[0].X, Z = interior[0].Z };
        var rooms = await s.CallAsync(
            "room",
            NativeOps.ListRooms,
            new Observations.ListRoomsRequest { Scope = s.Scope, Region = new Observations.Rectangle { Minimum = corner, Maximum = corner } },
            cancellationToken).ConfigureAwait(false);
        var observed = TypedReads.Observed("room", NativeOps.ListRooms, rooms, rooms.Outcome?.Observed);
        if (observed.Rooms is not [var room])
        {
            throw new AcceptanceException($"room: want the one built room at {corner.X},{corner.Z}, got {observed.Rooms.Count}");
        }

        var actual = room.Stats.FirstOrDefault(st => st.DefName == Impressiveness)?.Value;
        var problems = new List<string>();
        if (prediction.Role != room.Role)
        {
            problems.Add($"role {prediction.Role}, room {room.Role}");
        }

        if (!prediction.Impressiveness.IsKnown || actual is not double score || Math.Abs(prediction.Impressiveness.Value - score) > 1e-3)
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
        s.RecordWire("room", room);
        if (problems.Count > 0)
        {
            throw new AcceptanceException("prediction disagrees with the built room: " + string.Join("; ", problems));
        }
    }
}
