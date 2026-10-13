using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>rooms/reads</c> (Go <c>rooms/reads.go</c>): typed room reads (geometry, native stats, contents, cells) on the
/// lab: CAS snapshots, exact-id, cell and boundary lookups agree with each other and each room's cells with its own
/// extents.
/// </summary>
internal static class RoomsReads
{
    private static readonly string[] RequiredFields =
    [
        "role", "properRoom", "doorway", "outdoors", "psychologicallyOutdoors", "touchesMapEdge", "fogged", "openRoofCount", "cellCount", "temperatureC",
    ];

    internal static AcceptanceCase Case() => new()
    {
        Name = "rooms/reads",
        Scope = "Naturally generated rooms; read-only geometry, native stats and contents, no fixture spawning or construction orders.",
        Start = new FixtureStart { Op = "test/bed_assign_prepare", On = new LabStart() },
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        if (!Dyn.Contains(s.Names, "rimgovernor/observations_list_rooms"))
        {
            throw new AcceptanceException("missing rimgovernor/observations_list_rooms in discovery");
        }

        var identityBefore = await h.WireAsync("identity-before", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, before) = Harness.Outcome(identityBefore, "loaded");
        if (before["paused"].AsBool() != true)
        {
            throw new AcceptanceException("fresh debug game did not start paused");
        }

        var beforeContext = before["context"].AsMap();
        var identity = beforeContext?["identity"].AsMap() ?? [];
        JsonObject Scope() => new() { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } };

        var defaultReply = await h.WireAsync("typed-default", "observations_list_rooms", Scope(), cancellationToken).ConfigureAwait(false);
        var (_, defaultObserved) = Harness.Outcome(defaultReply, "observed");
        var rooms = defaultObserved["rooms"].AsSlice();
        if (rooms.Count == 0)
        {
            throw new AcceptanceException("typed room read returned no rooms");
        }

        var candidates = new List<JsonObject>();
        foreach (var raw in rooms)
        {
            var row = raw.AsMap() ?? [];
            CheckRoom(row, null);
            var properRoom = row["properRoom"].AsBool() == true;
            var outdoors = row["outdoors"].AsBool() == true;
            var cellCount = (int)row["cellCount"].AsNumber();
            if (properRoom && !outdoors && cellCount <= 4096)
            {
                candidates.Add(row);
            }
        }

        if (candidates.Count == 0)
        {
            throw new AcceptanceException("no indoor room found for populated acceptance");
        }

        candidates.Sort((a, b) =>
        {
            var ca = (int)a["cellCount"].AsNumber();
            var cb = (int)b["cellCount"].AsNumber();
            return ca != cb ? cb.CompareTo(ca) : string.CompareOrdinal(a["id"].AsString(), b["id"].AsString());
        });
        var target = candidates[0];
        var targetId = target["id"].AsString();

        JsonObject Exact() => Dyn.Merge(Scope(), new JsonObject { ["roomIds"] = new JsonArray(targetId) });
        var grid = await h.MapCellsAsync("map-cells", identity, cancellationToken).ConfigureAwait(false);
        var gridRooms = grid.RoomCells();
        var exactReply = await h.WireAsync("typed-exact-cells", "observations_list_rooms", Exact(), cancellationToken).ConfigureAwait(false);
        var (_, selected) = Harness.Outcome(exactReply, "observed");
        var selectedRows = selected["rooms"].AsSlice();
        if (selectedRows.Count != 1)
        {
            throw new AcceptanceException("exact room selection did not return exactly one room");
        }

        var selectedRow = selectedRows[0].AsMap() ?? [];
        CheckRoom(selectedRow, gridRooms);
        var repeatReply = await h.WireAsync("repeat", "observations_list_rooms", Exact(), cancellationToken).ConfigureAwait(false);
        var (_, repeatObserved) = Harness.Outcome(repeatReply, "observed");
        if (!Dyn.DeepEqual(repeatObserved["rooms"], selected["rooms"]))
        {
            throw new AcceptanceException("repeating the exact room read returned a different snapshot");
        }

        var center = selectedRow["center"];
        var region = new JsonObject { ["minimum"] = center?.DeepClone(), ["maximum"] = center?.DeepClone() };
        var byCellReply = await h.WireAsync("cell-intersection", "observations_list_rooms", Dyn.Merge(Exact(), new JsonObject { ["region"] = region }), cancellationToken).ConfigureAwait(false);
        var (_, byCellObserved) = Harness.Outcome(byCellReply, "observed");
        if (!Dyn.DeepEqual(byCellObserved["rooms"], selected["rooms"]))
        {
            throw new AcceptanceException("cell-intersection lookup returned a different room than the exact-id lookup");
        }

        var absentReply = await h.WireAsync("unknown-id", "observations_list_rooms", Dyn.Merge(Scope(), new JsonObject { ["roomIds"] = new JsonArray("absent-room") }), cancellationToken).ConfigureAwait(false);
        var (_, absentObserved) = Harness.Outcome(absentReply, "observed");
        if (absentObserved["rooms"].AsSlice().Count != 0)
        {
            throw new AcceptanceException("unknown room id unexpectedly matched a room");
        }

        var boundaryReply = await h.WireAsync("typed-boundary", "observations_list_rooms", Dyn.Merge(Exact(), new JsonObject { ["includeBoundary"] = true }), cancellationToken).ConfigureAwait(false);
        var (_, boundaryObserved) = Harness.Outcome(boundaryReply, "observed");
        var boundaryRows = boundaryObserved["rooms"].AsSlice();
        if (boundaryRows.Count != 1)
        {
            throw new AcceptanceException("typed boundary read did not return exactly one room");
        }

        var boundaryRow = boundaryRows[0].AsMap() ?? [];
        CheckRoom(boundaryRow, gridRooms);
        if (SumUnits(boundaryRow["contents"]) <= SumUnits(target["contents"]))
        {
            throw new AcceptanceException("no populated boundary buildings tested (boundary contents did not exceed indoor-only contents)");
        }

        var invalid = new (string Label, JsonObject Change)[]
        {
            ("duplicate-id", new JsonObject { ["roomIds"] = new JsonArray(targetId, targetId) }),
            ("unknown-write", new JsonObject { ["set"] = true }),
        };
        foreach (var (label, change) in invalid)
        {
            var reply = await h.WireAsync(label, "observations_list_rooms", Dyn.Merge(Scope(), change), cancellationToken).ConfigureAwait(false);
            var code = NativeAssert.FailureCode(reply);
            if (code != "FAILURE_CODE_INVALID_REQUEST")
            {
                throw new AcceptanceException($"{label}: expected FAILURE_CODE_INVALID_REQUEST, got \"{code}\"");
            }
        }

        var identityAfterReply = await h.WireAsync("identity-after", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, after) = Harness.Outcome(identityAfterReply, "loaded");
        if (after["paused"].AsBool() != true)
        {
            throw new AcceptanceException("game unexpectedly resumed during the read pass");
        }

        if (!Dyn.DeepEqual(after["context"], beforeContext))
        {
            throw new AcceptanceException("identity/tick changed during a read-only pass");
        }

        report["rooms"] = rooms.Count;
        report["indoor_room"] = targetId;
        report["indoor_cells"] = (int)target["cellCount"].AsNumber();
    }

    private static double SumUnits(JsonNode? value)
    {
        var total = 0.0;
        foreach (var raw in value.AsSlice())
        {
            total += raw.AsMap()?["units"].AsNumber() ?? -1;
        }

        return total;
    }

    /// <summary>
    /// Validates one typed room row's CAS snapshot and, given the map grid's rooms, its cell geometry (the grid cells
    /// carrying its gridRoom key) against its own cellCount, center and extents.
    /// </summary>
    private static void CheckRoom(JsonObject row, IReadOnlyDictionary<string, IReadOnlyList<Cell>>? gridRooms)
    {
        var id = row["id"].ToJson();
        try
        {
            NativeAssert.RequireSnapshot(row["snapshot"]);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"room {id} missing a populated CAS snapshot: {e.Message}", e);
        }

        foreach (var field in RequiredFields)
        {
            if (!row.ContainsKey(field))
            {
                throw new AcceptanceException($"room {id} lacks {field}");
            }
        }

        if (gridRooms is null)
        {
            return;
        }

        var actual = gridRooms.TryGetValue(row["gridRoom"].AsString(), out var cells) ? cells : [];
        if (actual.Count == 0 || actual.Count != (int)row["cellCount"].AsNumber())
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"room {id} grid cells ({actual.Count}) do not match cellCount"));
        }

        var center = row["center"].AsMap();
        var centerCell = (center?["x"].AsNumber() ?? -1, center?["z"].AsNumber() ?? -1);
        if (!actual.Any(c => (double)c.X == centerCell.Item1 && (double)c.Z == centerCell.Item2))
        {
            throw new AcceptanceException($"room {id} center is not among the room's own cells");
        }

        var minX = actual.Min(c => c.X);
        var minZ = actual.Min(c => c.Z);
        var maxX = actual.Max(c => c.X);
        var maxZ = actual.Max(c => c.Z);
        var extents = row["extents"].AsMap();
        var minimum = extents?["minimum"].AsMap();
        var maximum = extents?["maximum"].AsMap();
        if (minimum?["x"].AsNumber() != minX || minimum["z"].AsNumber() != minZ || maximum?["x"].AsNumber() != maxX || maximum["z"].AsNumber() != maxZ)
        {
            throw new AcceptanceException($"room {id} extents do not match the room's own cells");
        }
    }
}
