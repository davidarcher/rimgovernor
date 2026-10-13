using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>lab/spawn</c> (Go <c>spawn.go</c>).</summary>
internal static class LabSpawn
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "lab/spawn",
        Scope = "Lab runner: test/lab_start hands the case a blank lab with its colonists, and a building and a pawn spawned by test/lab_spawn read back through the typed observations.",
        Start = new LabStart { Colonists = 2 },
        RequiredOps = [DebugGame.LabSpawnTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var prepared = s.Prepared ?? [];
        var n = prepared["colonists"].AsSlice().Count;
        if (n != 2)
        {
            throw new AcceptanceException($"lab start: want 2 colonists, got {n}: {prepared.ToJson()}");
        }

        var (cx, cz) = CaseSupport.Center(s);
        var h = s.Harness;
        var (wall, _) = await DebugGame.LabSpawnAsync(h, new LabThing { Def = "Wall", Stuff = "WoodLog", X = cx, Z = cz + 4 }, cancellationToken).ConfigureAwait(false);
        var (animal, pawn) = await DebugGame.LabSpawnAsync(h, new LabThing { Def = "Muffalo", X = cx + 4, Z = cz, Unowned = true }, cancellationToken).ConfigureAwait(false);
        if (pawn["kind"].AsString() != "pawn")
        {
            throw new AcceptanceException($"lab_spawn Muffalo: kind {pawn["kind"].ToJson()}, not a pawn: {pawn.ToJson()}");
        }

        s.Report["spawned"] = new JsonObject { ["wall"] = wall, ["animal"] = animal };
        var reply = await h.WireAsync(
            "wall",
            "observations_list_buildings",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = s.Identity.DeepClone() },
                ["ids"] = new JsonArray(wall),
            },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        var rows = observed["buildings"].AsSlice();
        if (rows.Count != 1)
        {
            throw new AcceptanceException($"list_buildings {wall}: want one row, got {observed.ToJson()}");
        }

        var row = rows[0].AsMap() ?? [];
        if (row["stuff"].AsString() != "WoodLog")
        {
            throw new AcceptanceException($"wall {wall}: want WoodLog stuff, got {row.ToJson()}");
        }

        s.Report["wall"] = row;
    }
}
