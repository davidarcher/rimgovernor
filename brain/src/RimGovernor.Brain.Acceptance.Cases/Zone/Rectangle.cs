using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>zone/rectangle</c> (Go <c>rectangle.go</c>).</summary>
internal static class ZoneRectangle
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "zone/rectangle",
        Scope = "Native stockpile rectangle placement uses vanilla cell eligibility, keeps both components across a wall barrier and ground holding Steel, skips an existing player stockpile without changing it, and replays all created IDs without duplicates. A Go snapshot cannot verify the native designator, zone grid, hauling settings or replay cache.",
        Start = new FixtureStart { On = new LabStart(), Op = "test/stockpile_rectangle_prepare" },
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        var x = (int)prepared["x"].AsNumber();
        var z = (int)prepared["z"].AsNumber();
        var playerId = prepared["playerZoneId"].AsString();
        if (playerId.Length == 0)
        {
            throw new AcceptanceException($"missing prepared player zone: {prepared.ToJson()}");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);
        var intent = new JsonObject
        {
            ["label"] = "Rectangle storage",
            ["kind"] = "ZONE_TYPE_STOCKPILE",
            ["addCells"] = new JsonObject
            {
                ["rectangle"] = new JsonObject { ["origin"] = new JsonObject { ["x"] = x, ["z"] = z }, ["width"] = 7, ["height"] = 5 },
            },
            ["stockpile"] = new JsonObject
            {
                ["priority"] = "STORAGE_PRIORITY_IMPORTANT",
                ["preset"] = "FILTER_PRESET_NOTHING",
                ["filter"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["thingDef"] = "Steel" }) },
            },
        };

        async Task<JsonObject> Apply(string label)
        {
            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject { ["key"] = "rectangle-stockpile", ["zone"] = intent.DeepClone() }),
                },
                cancellationToken).ConfigureAwait(false);
            var results = reply["results"].AsSlice();
            if (results.Count != 1)
            {
                throw new AcceptanceException($"{label}: {reply.ToJson()}");
            }

            var result = results[0].AsMap() ?? [];
            return CaseSupport.AppliedEffect(result, "zone")
                ?? throw new AcceptanceException($"{label}: missing applied stockpile evidence: {result.ToJson()}");
        }

        var first = await Apply("rectangle-create").ConfigureAwait(false);
        var rows = first["created"].AsSlice();
        if (rows.Count != 2)
        {
            throw new AcceptanceException($"expected both components: {first.ToJson()}");
        }

        var ids = new List<string> { playerId };
        var actual = new Dictionary<string, List<Cell>>(StringComparer.Ordinal);
        var expected = new HashSet<Cell>();
        for (var dx = 0; dx < 7; dx++)
        {
            for (var dz = 0; dz < 5; dz++)
            {
                if (dx != 3 && !(dx == 1 && dz == 2) && !(dx == 6 && dz == 4))
                {
                    expected.Add(new Cell(x + dx, z + dz));
                }
            }
        }

        var count = 0;
        foreach (var raw in rows)
        {
            var row = raw.AsMap() ?? [];
            var id = row["zoneId"].AsString();
            if (id.Length == 0 || id == playerId || actual.ContainsKey(id))
            {
                throw new AcceptanceException($"invalid created identity: {row.ToJson()}");
            }

            ids.Add(id);
            actual[id] = [];
            foreach (var rawCell in row["cells"].AsSlice())
            {
                var cell = rawCell.AsMap() ?? [];
                var c = new Cell((int)cell["x"].AsNumber(), (int)cell["z"].AsNumber());
                if (!expected.Remove(c))
                {
                    throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"unexpected/duplicate cell {c}"));
                }

                actual[id].Add(c);
                count++;
            }
        }

        if (count != 28 || expected.Count != 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"lost usable cells: count={count} missing={string.Join(' ', expected)}"));
        }

        var grid = await h.MapCellsAsync("rectangle-grid", identity, cancellationToken).ConfigureAwait(false);
        var byZone = grid.ZoneCells();
        foreach (var (id, cells) in actual)
        {
            var gridCells = byZone.GetValueOrDefault(id) ?? [];
            if (gridCells.Count != cells.Count)
            {
                throw new AcceptanceException($"zone {id}: receipt/grid mismatch: {string.Join(' ', cells)} {string.Join(' ', gridCells)}");
            }

            var set = cells.ToHashSet();
            foreach (var c in gridCells)
            {
                if (!set.Contains(c))
                {
                    throw new AcceptanceException($"zone {id} unexpected grid cell {c}");
                }
            }
        }

        var zonesReply = await h.WireAsync(
            "rectangle-zone-settings",
            "observations_list_zones",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["ids"] = new JsonArray([.. ids.Select(id => (JsonNode?)id)]),
                ["includeFilter"] = true,
            },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(zonesReply, "observed");
        var zones = observed["zones"].AsSlice();
        if (zones.Count != 3)
        {
            throw new AcceptanceException($"expected three zones: {observed.ToJson()}");
        }

        foreach (var raw in zones)
        {
            var row = raw.AsMap() ?? [];
            var id = row["id"].AsString();
            var allowed = row["filter"].AsMap()?["allowedDefNames"].AsSlice() ?? [];
            if (id == playerId)
            {
                if (row["priority"].AsString() != "Low" || allowed.Count != 0)
                {
                    throw new AcceptanceException($"player zone changed: {row.ToJson()}");
                }
            }
            else if (row["priority"].AsString() != "Important" || allowed.Count != 1 || allowed[0].AsString() != "Steel")
            {
                throw new AcceptanceException($"created settings mismatch: {row.ToJson()}");
            }
        }

        // Creation receipts remain recoverable after ordinary replay entries age out.
        var churn = new JsonArray();
        for (var i = 0; i < 260; i++)
        {
            churn.Add(new JsonObject
            {
                ["key"] = $"rectangle-settings-{CaseSupport.Tag(i)}",
                ["zone"] = new JsonObject
                {
                    ["zone"] = new JsonObject { ["id"] = ids[1] },
                    ["stockpile"] = new JsonObject { ["priority"] = "STORAGE_PRIORITY_IMPORTANT" },
                },
            });
        }

        _ = await h.WireAsync("rectangle-replay-churn", "operations_apply", new JsonObject { ["identity"] = identity.DeepClone(), ["actions"] = churn }, cancellationToken).ConfigureAwait(false);
        var replay = await Apply("rectangle-replay").ConfigureAwait(false);
        if (!Dyn.DeepEqual(first, replay))
        {
            throw new AcceptanceException($"replay lost creation identities: {replay.ToJson()}");
        }

        grid = await h.MapCellsAsync("rectangle-replay-grid", identity, cancellationToken).ConfigureAwait(false);
        var after = grid.ZoneCells();
        foreach (var id in ids)
        {
            var before = byZone.GetValueOrDefault(id) ?? [];
            var now = after.GetValueOrDefault(id) ?? [];
            if (!before.SequenceEqual(now))
            {
                throw new AcceptanceException($"replay changed zone {id}");
            }
        }

        s.Report["created_components"] = rows.Count;
        s.Report["created_cells"] = count;
        s.Report["player_zone_unchanged"] = true;
        s.Report["replayed_without_duplicates"] = true;
    }
}
