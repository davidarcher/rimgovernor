using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>wall/replace</c> (Go <c>replace.go</c>).</summary>
internal static class WallReplace
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "wall/replace",
        Scope = "In-place wall replacement (#2529): a BuildingIntent with replace_wall over a built steel wall of a roofed, enclosed room " +
            "swaps it for a wood wall through vanilla's replacement blueprint, and on every single tick from placement to completion the cell " +
            "is impassable and the room stays enclosed. A swap that would drop a roof's last holder, a cell with no built wall and a " +
            "non-wall replacement are refused with typed codes; a plain new-ring wall still gets the Standable Frame. " +
            "A Go snapshot cannot prove the cloned Impassable Frame def, the Harmony frame choice, vanilla's wipe-and-spawn step or room regions.",
        Start = new LabStart(),
        RequiredOps = ["test/wall_replacement"],
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var id = s.Identity;
        var report = s.Report;
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "wall-replace-authority", id, cancellationToken).ConfigureAwait(false);

        async Task<JsonObject> Call(string label, string action, WallCell c, int ticks)
        {
            var row = await h.CallAsync(label, "test/wall_replacement", new JsonObject { ["action"] = action, ["x"] = c.X, ["z"] = c.Z, ["ticks"] = ticks }, cancellationToken).ConfigureAwait(false);
            return row["success"].AsBool() == true ? row : throw new AcceptanceException($"wall_replacement {action} refused: {row.ToJson()}");
        }

        var prepared = await Call("replace-prepare", "prepare", default, 0).ConfigureAwait(false);
        var centre = prepared["centre"];
        var cx = (int)centre.Member("x").AsNumber();
        var cz = (int)centre.Member("z").AsNumber();
        var target = new WallCell(cx + 3, cz);
        var pillar = new WallCell(cx + 20, cz);
        var empty = new WallCell(cx + 12, cz + 8);

        Task<JsonObject> Apply(string label, string def, string stuff, WallCell c, bool replace)
        {
            var building = new JsonObject
            {
                ["placement"] = new JsonObject { ["defName"] = def, ["stuff"] = stuff, ["x"] = c.X, ["z"] = c.Z, ["rotation"] = "ROTATION_NORTH" },
            };
            if (replace)
            {
                building["replaceWall"] = true;
            }

            return ApplyIntents.ApplyOneAsync(h, label, id, label, new JsonObject { ["building"] = building }, cancellationToken);
        }

        async Task Refused(string label, string def, string stuff, WallCell c, string code)
        {
            var result = await Apply(label, def, stuff, c, true).ConfigureAwait(false);
            if (result["refused"].AsMap() is not { } refusal || refusal["code"].AsString() != code)
            {
                throw new AcceptanceException($"{label}: expected {code}, got {result.ToJson()}");
            }
        }

        await Refused("replace-no-wall", "Wall", "WoodLog", empty, "FAILURE_CODE_NO_WALL_TO_REPLACE").ConfigureAwait(false);
        await Refused("replace-not-a-wall", "Door", "WoodLog", target, "FAILURE_CODE_WALL_REPLACEMENT_REFUSED").ConfigureAwait(false);
        await Refused("replace-last-roof-holder", "Wall", "WoodLog", pillar, "FAILURE_CODE_WALL_REPLACEMENT_STRANDS_ROOF").ConfigureAwait(false);

        var result = await Apply("replace-wall", "Wall", "WoodLog", target, true).ConfigureAwait(false);
        if (result["applied"].AsMap() is null)
        {
            throw new AcceptanceException($"replacement not applied: {result.ToJson()}");
        }

        var placed = await Call("replace-placed", "audit", target, 0).ConfigureAwait(false);
        if (placed["impassable"].AsBool() != true || placed["builtStuff"].AsString() != "Steel")
        {
            throw new AcceptanceException($"the old wall must stand while the blueprint waits: {placed.ToJson()}");
        }

        report["placed"] = placed;
        for (var elapsed = 0; elapsed < 3000; elapsed += 100)
        {
            var row = await Call("replace-run", "run", target, 100).ConfigureAwait(false);
            if (row["violations"].AsNumber() != 0)
            {
                throw new AcceptanceException($"the enclosure opened during the swap: {row.ToJson()}");
            }

            if (row["builtDef"].AsString() == "Wall" && row["builtStuff"].AsString() == "WoodLog")
            {
                report["completed"] = row;
                await NewRingStaysStandableAsync(s, Call, Apply, empty).ConfigureAwait(false);
                return;
            }
        }

        throw new AcceptanceException("replacement wall did not finish within 3000 ticks");
    }

    /// <summary>Places a plain wall on an empty cell: with no built wall under it, its Frame is still the Standable one (#2314).</summary>
    private static async Task NewRingStaysStandableAsync(
        ISession s,
        Func<string, string, WallCell, int, Task<JsonObject>> call,
        Func<string, string, string, WallCell, bool, Task<JsonObject>> apply,
        WallCell site)
    {
        var result = await apply("ring-wall", "Wall", "WoodLog", site, false).ConfigureAwait(false);
        if (result["applied"].AsMap() is null)
        {
            throw new AcceptanceException($"new-ring wall not applied: {result.ToJson()}");
        }

        for (var elapsed = 0; elapsed < 3000; elapsed += 5)
        {
            var row = await call("ring-run", "run", site, 5).ConfigureAwait(false);
            if (row["frameDef"].AsString() != string.Empty)
            {
                if (row["frameStandable"].AsBool() != true || row["frameDef"].AsString() != "Frame_Wall")
                {
                    throw new AcceptanceException($"a new-ring wall must keep the Standable Frame: {row.ToJson()}");
                }

                s.Report["new_ring"] = row;
                return;
            }
        }

        throw new AcceptanceException("new-ring wall never became a frame");
    }
}
