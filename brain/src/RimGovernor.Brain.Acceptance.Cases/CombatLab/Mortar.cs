using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/mortar</c> (Go <c>mortar.go</c>).</summary>
internal static class CombatLabMortar
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/mortar",
        Scope = "combat.orders on buildings (#930, #931), native op contracts no snapshot can prove: on lab-open plus a hostile crashed ship part 30 cells north, "
            + "a player mortar with one HE shell behind the line and a stone chunk, one call orders an attack on the ship part, a mortar_fire at the part "
            + "with a man_mortar crew, and the same pair on the chunk; the first three apply (AttackStatic, the aim, ManTurret) and the chunk pair refuses not_a_mortar (#1202). One tick later the rifleman "
            + "shoots the part and the crew mans the mortar; then a mortar_fire with no target applies on the mortar (clearing its forced target) and refuses not_a_mortar on the chunk (#1235).",
        Start = new LabStart { Colonists = 3 },
        RequiredOps = [DebugGame.LabStartTool, FixtureStage.Tool, CombatFixtures.LabTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var report = s.Report;
        var cx = 0;
        var cz = 0;
        var staged = await CombatStage.StageAsync(h, "lab-open", cancellationToken, (f, x, z) =>
        {
            cx = x;
            cz = z;
            f.Things.Add(new FixtureThing { Def = "DefoliatorShipPart", X = x, Z = z + 20, Hostile = true });
            f.Things.Add(new FixtureThing { Def = "Turret_Mortar", X = x, Z = z - 15 });
            f.Things.Add(new FixtureThing { Def = "Shell_HighExplosive", X = x + 2, Z = z - 16, Item = true });
        }).ConfigureAwait(false);
        var colonists = staged.Colonists();
        var things = staged.Things;
        var part = things[^3];
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "combat-mortar-acquire", identity, cancellationToken).ConfigureAwait(false);
        var orders = new JsonArray();
        foreach (var id in colonists.GetRange(0, 3))
        {
            orders.Add(new JsonObject { ["pawn"] = LabOps.PawnTarget(id), ["draft"] = new JsonObject() });
        }

        JsonObject Mortar() => CaseSupport.Cell(cx, cz - 15);
        JsonObject Chunk() => CaseSupport.Cell(cx - 3, cz - 9);
        JsonObject Target() => CaseSupport.Cell(cx, cz + 20);
        orders.Add(new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[0]), ["attack"] = LabOps.PawnTarget(part) });
        orders.Add(new JsonObject { ["mortarFire"] = new JsonObject { ["mortar"] = Mortar(), ["target"] = Target() } });
        orders.Add(new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[1]), ["manMortar"] = Mortar() });
        orders.Add(new JsonObject { ["mortarFire"] = new JsonObject { ["mortar"] = Chunk(), ["target"] = Target() } });
        orders.Add(new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[2]), ["manMortar"] = Chunk() });
        var results = await LabOps.IssueAsync(h, identity, "combat-mortar-1", orders, cancellationToken).ConfigureAwait(false);
        report["results"] = new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())]);
        for (var i = 0; i < 3; i++)
        {
            if (results[i]["applied"].AsBool() != true)
            {
                throw new AcceptanceException($"draft {CaseSupport.Tag(i)} refused: {results[i].ToJson()}");
            }
        }

        foreach (var (i, job) in new[] { (3, "AttackStatic"), (5, "ManTurret") })
        {
            if (results[i]["applied"].AsBool() != true || results[i]["jobDef"].AsString() != job)
            {
                throw new AcceptanceException($"order {CaseSupport.Tag(i)}: want applied with job {job}, got {results[i].ToJson()}");
            }
        }

        if (results[4]["applied"].AsBool() != true)
        {
            throw new AcceptanceException($"order 4 (mortar_fire): want applied, got {results[4].ToJson()}");
        }

        foreach (var i in new[] { 6, 7 })
        {
            if (results[i]["applied"].AsBool() == true || results[i]["refusal"].AsString() != "not_a_mortar")
            {
                throw new AcceptanceException($"order {CaseSupport.Tag(i)}: want refusal not_a_mortar, got {results[i].ToJson()}");
            }
        }

        var after = await LabOps.TickReadAsync(h, cancellationToken).ConfigureAwait(false);
        report["after"] = after.PawnsJson();
        var rifleman = after.Row(colonists[0]);
        if (rifleman["job"].AsString() != "AttackStatic" || rifleman["jobThing"].AsString() != part)
        {
            throw new AcceptanceException($"rifleman does not shoot the ship part: {rifleman.ToJson()}");
        }

        var crew = after.Row(colonists[1]);
        if (crew["job"].AsString() != "ManTurret" || crew["jobThing"].AsString() != things[^2])
        {
            throw new AcceptanceException($"crew does not man the mortar: {crew.ToJson()}");
        }

        // A mortar_fire with no target clears the forced target.
        var cleared = await LabOps.IssueAsync(
            h,
            identity,
            "combat-mortar-2",
            new JsonArray(
                new JsonObject { ["mortarFire"] = new JsonObject { ["mortar"] = Mortar() } },
                new JsonObject { ["mortarFire"] = new JsonObject { ["mortar"] = Chunk() } }),
            cancellationToken).ConfigureAwait(false);
        report["cleared"] = new JsonArray([.. cleared.Select(r => (JsonNode?)r.DeepClone())]);
        if (cleared[0]["applied"].AsBool() != true)
        {
            throw new AcceptanceException($"clear on the mortar: want applied, got {cleared[0].ToJson()}");
        }

        if (cleared[1]["applied"].AsBool() == true || cleared[1]["refusal"].AsString() != "not_a_mortar")
        {
            throw new AcceptanceException($"clear on the chunk: want refusal not_a_mortar, got {cleared[1].ToJson()}");
        }
    }
}
