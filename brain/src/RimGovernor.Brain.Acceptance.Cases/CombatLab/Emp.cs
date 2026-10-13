using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/emp</c> (Go <c>emp.go</c>).</summary>
internal static class CombatLabEmp
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/emp",
        Scope = "EMP ground targeting (#1049), vanilla physics no snapshot can prove: on lab-ranged with rifleman 0 holding EMP grenades and the four raiders "
            + "downed in a clump 11 cells past the sandbags, each wearing a charged shield belt, one combat.orders call drafts the thrower and orders attack_ground "
            + "at the clump's centre; within 600 ticks every raider's shield energy reads 0.",
        Start = new LabStart { Colonists = 4 },
        RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    // empKit arms colonist 0 with EMP grenades and stages the raiders downed (so they hold still) in a clump within
    // throwing range, shield belts on.
    private static void Kit(LabFixture f, int cx, int cz)
    {
        (int, int)[] clump = [(-1, 2), (0, 2), (1, 2), (0, 3)];
        var h = 0;
        foreach (var p in f.Pawns)
        {
            if (p.Side == CombatFixtures.Colonist && p.Index == 0)
            {
                p.Weapon = "Weapon_GrenadeEMP";
                p.WeaponStuff = "";
            }
            else if (p.Side == CombatFixtures.Hostile)
            {
                p.X = cx + clump[h].Item1;
                p.Z = cz + clump[h].Item2;
                p.Downed = true;
                p.Apparel = "Apparel_ShieldBelt";
                h++;
            }
        }
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var report = s.Report;
        var cx = 0;
        var cz = 0;
        var staged = await CombatStage.StageAsync(h, "lab-ranged", cancellationToken, (f, x, z) =>
        {
            Kit(f, x, z);
            cx = x;
            cz = z;
        }).ConfigureAwait(false);
        var colonists = staged.Colonists();
        var hostiles = staged.Hostiles();
        if (colonists.Count != 4 || hostiles.Count != 4)
        {
            throw new AcceptanceException($"staged {CaseSupport.Tag(colonists.Count)} colonists and {CaseSupport.Tag(hostiles.Count)} hostiles, want 4 and 4");
        }

        var before = await LabOps.TickReadAsync(h, cancellationToken).ConfigureAwait(false);
        foreach (var id in hostiles)
        {
            var e = before.Row(id)["shield"].AsNumber();
            if (e <= 0)
            {
                throw new AcceptanceException($"raider {id} shield energy {e} before the EMP, want charged: {before.Row(id).ToJson()}");
            }
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "combat-emp-acquire", identity, cancellationToken).ConfigureAwait(false);
        var results = await LabOps.IssueAsync(
            h,
            identity,
            "combat-emp-1",
            new JsonArray(
                new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[0]), ["draft"] = new JsonObject() },
                new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[0]), ["attackGround"] = CaseSupport.Cell(cx, cz + 2) }),
            cancellationToken).ConfigureAwait(false);
        report["results"] = new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())]);
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i]["applied"].AsBool() != true)
            {
                throw new AcceptanceException($"order {CaseSupport.Tag(i)} refused: {results[i].ToJson()}");
            }
        }

        LabState after = new();
        for (var ticked = 0; ticked < 600; ticked += 60)
        {
            after = await LabOps.TickReadNAsync(h, 60, cancellationToken).ConfigureAwait(false);
            var dropped = 0;
            foreach (var id in hostiles)
            {
                if (after.Row(id)["shield"] is { } v && v.AsNumber() == 0)
                {
                    dropped++;
                }
            }

            report["ticks"] = ticked + 60;
            report["dropped"] = dropped;
            if (dropped == hostiles.Count)
            {
                return;
            }
        }

        var rows = new JsonArray([.. hostiles.Select(id => (JsonNode?)after.Row(id).DeepClone())]);
        throw new AcceptanceException($"shields still up 600 ticks after the EMP: thrower {after.Row(colonists[0]).ToJson()}, raiders {rows.ToJson()}");
    }
}
