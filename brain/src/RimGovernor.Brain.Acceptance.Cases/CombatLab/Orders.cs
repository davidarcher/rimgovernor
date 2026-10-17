using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/orders</c> (Go <c>orders.go</c>).</summary>
internal static class CombatLabOrders
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/orders",
        Scope = "Batched combat.orders (#850), a native op contract no snapshot can prove: on lab-open (five colonists, one with frag grenades, a player door) one call "
            + "orders attack_ground, move, attack, fire_mode hold, hold_position, move-then-stop and door hold_open, plus an unowned hostile and a non-door cell; "
            + "every order applies or refuses with its named reason, and one tick later each pawn's job, target, fire mode and the door read back as ordered. A second call closes the door.",
        Start = new LabStart { Colonists = 5 },
        RequiredOps = [DebugGame.LabStartTool, FixtureStage.Tool, CombatFixtures.LabTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    // ordersKit adds what the seven orders need to lab-open: two more riflemen (hold and stop), frag grenades on colonist
    // 0 (attack_ground) and a door behind the line.
    private static void OrdersKit(LabFixture f, int cx, int cz)
    {
        f.Colonists = 5;
        foreach (var p in f.Pawns)
        {
            if (p.Side == CombatFixtures.Colonist && p.Index == 0)
            {
                p.Weapon = "Weapon_GrenadeFrag";
                p.WeaponStuff = "";
            }
        }

        f.Pawns.Add(new LabPawn { Side = CombatFixtures.Colonist, Index = 3, X = cx - 5, Z = cz - 10, Weapon = CombatFixtures.Rifle });
        f.Pawns.Add(new LabPawn { Side = CombatFixtures.Colonist, Index = 4, X = cx + 5, Z = cz - 10, Weapon = CombatFixtures.Rifle });
        f.Things.Add(new FixtureThing { Def = "Door", Stuff = "WoodLog", X = cx, Z = cz - 13 });
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var report = s.Report;
        var cx = 0;
        var cz = 0;
        var staged = await CombatStage.StageAsync(h, "lab-open", cancellationToken, (f, x, z) =>
        {
            OrdersKit(f, x, z);
            cx = x;
            cz = z;
        }).ConfigureAwait(false);
        var colonists = staged.Colonists();
        var hostiles = staged.Hostiles();
        if (colonists.Count != 5 || hostiles.Count != 3)
        {
            throw new AcceptanceException($"staged {CaseSupport.Tag(colonists.Count)} colonists and {CaseSupport.Tag(hostiles.Count)} hostiles, want 5 and 3");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "combat-orders-acquire", identity, cancellationToken).ConfigureAwait(false);
        await LabOps.DraftAllAsync(h, identity, "draft", colonists, cancellationToken).ConfigureAwait(false);
        var ground = () => CaseSupport.Cell(cx - 2, cz - 3);
        var moveTo = () => CaseSupport.Cell(cx + 7, cz - 12);
        var stopFrom = () => CaseSupport.Cell(cx + 5, cz - 15);
        var door = () => CaseSupport.Cell(cx, cz - 13);
        var orders = new JsonArray
        {
            new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[0]), ["attackGround"] = ground() },
            new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[1]), ["move"] = moveTo() },
            new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[2]), ["attack"] = LabOps.PawnTarget(hostiles[2]) },
            new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[3]), ["fireMode"] = "COMBAT_FIRE_MODE_HOLD" },
            new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[3]), ["holdPosition"] = new JsonObject() },
            new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[4]), ["move"] = stopFrom() },
            new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[4]), ["stop"] = new JsonObject() },
            new JsonObject { ["door"] = new JsonObject { ["cell"] = door(), ["mode"] = "COMBAT_DOOR_MODE_HOLD_OPEN" } },
            new JsonObject { ["pawn"] = LabOps.PawnTarget(hostiles[0]), ["move"] = moveTo() },
            new JsonObject { ["door"] = new JsonObject { ["cell"] = CaseSupport.Cell(cx + 10, cz), ["mode"] = "COMBAT_DOOR_MODE_CLOSE" } },
        };
        string[] wantJob = ["AttackStatic", "Goto", "AttackStatic", "", "Wait_Combat", "Goto", "", "", "", ""];
        var wantRefusal = new Dictionary<int, string> { [8] = "not_drafted", [9] = "not_a_door" };
        var results = await LabOps.IssueAsync(h, identity, "combat-orders-1", orders, cancellationToken).ConfigureAwait(false);
        report["results"] = new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())]);
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var applied = r["applied"].AsBool() == true;
            if (wantRefusal.TryGetValue(i, out var want))
            {
                if (applied || r["refusal"].AsString() != want)
                {
                    throw new AcceptanceException($"order {CaseSupport.Tag(i)}: want refusal {want}, got {r.ToJson()}");
                }

                continue;
            }

            if (!applied || r["jobDef"].AsString() != wantJob[i])
            {
                throw new AcceptanceException($"order {CaseSupport.Tag(i)}: want applied with job \"{wantJob[i]}\", got {r.ToJson()}");
            }
        }

        var after = await LabOps.TickReadAsync(h, cancellationToken).ConfigureAwait(false);
        report["after"] = AfterJson(after);

        void CheckBase(int i, string job, bool forced)
        {
            var p = after.Row(colonists[i]);
            if (p["job"].AsString() != job)
            {
                throw new AcceptanceException($"colonist {CaseSupport.Tag(i)} job {p["job"].ToJson()}, want {job}: {p.ToJson()}");
            }

            if ((p["playerForced"].AsBool() == true) != forced)
            {
                throw new AcceptanceException($"colonist {CaseSupport.Tag(i)} playerForced {(p["playerForced"].AsBool() == true ? "true" : "false")}, want {(forced ? "true" : "false")}: {p.ToJson()}");
            }
        }

        void CheckThing(int i, string job, bool forced, string target)
        {
            CheckBase(i, job, forced);
            var p = after.Row(colonists[i]);
            if (p["jobThing"].AsString() != target)
            {
                throw new AcceptanceException($"colonist {CaseSupport.Tag(i)} targets {p["jobThing"].ToJson()}, want {target}");
            }
        }

        void CheckCell(int i, string job, bool forced, JsonObject target)
        {
            CheckBase(i, job, forced);
            var p = after.Row(colonists[i]);
            var got = p["jobCell"].AsMap() ?? [];
            if (got["x"].AsNumber() != target["x"].AsNumber() || got["z"].AsNumber() != target["z"].AsNumber())
            {
                throw new AcceptanceException($"colonist {CaseSupport.Tag(i)} targets cell {got.ToJson()}, want {target.ToJson()}");
            }
        }

        CheckCell(0, "AttackStatic", true, ground());
        CheckCell(1, "Goto", true, moveTo());
        CheckThing(2, "AttackStatic", true, hostiles[2]);
        CheckBase(3, "Wait_Combat", true);
        var fireAtWill = after.Row(colonists[3])["fireAtWill"].AsBool();
        if (fireAtWill is null or true)
        {
            throw new AcceptanceException($"colonist 3 fire at will {after.Row(colonists[3])["fireAtWill"].ToJson()} after hold");
        }

        var p4 = after.Row(colonists[4]);
        if (p4["job"].AsString() == "Goto")
        {
            throw new AcceptanceException($"colonist 4 still moving after stop: {p4.ToJson()}");
        }

        if (p4["playerForced"].AsBool() == true)
        {
            throw new AcceptanceException($"colonist 4 holds a player-forced job after stop: {p4.ToJson()}");
        }

        if (!after.Door(cx, cz - 13))
        {
            throw new AcceptanceException("door hold-open False after hold_open (<nil>)");
        }

        if (after.PhysicalDoor(cx, cz - 13))
        {
            throw new AcceptanceException("latch alone opened door: True (<nil>)");
        }

        _ = await LabOps.IssueAsync(h, identity, "door-passage", new JsonArray(new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[4]), ["move"] = door() }), cancellationToken).ConfigureAwait(false);
        var opened = false;
        for (var ticks = 0; ticks < 1000; ticks += 20)
        {
            after = await LabOps.TickReadNAsync(h, 20, cancellationToken).ConfigureAwait(false);
            opened = after.PhysicalDoor(cx, cz - 13);
            if (opened)
            {
                break;
            }
        }

        if (!opened)
        {
            throw new AcceptanceException("ordinary pawn passage did not open held door");
        }

        _ = await LabOps.IssueAsync(h, identity, "door-clearance", new JsonArray(new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[4]), ["move"] = CaseSupport.Cell(cx + 5, cz - 10) }), cancellationToken).ConfigureAwait(false);
        _ = await LabOps.TickReadNAsync(h, 600, cancellationToken).ConfigureAwait(false);
        var closed = await LabOps.IssueAsync(
            h,
            identity,
            "combat-orders-2",
            new JsonArray(new JsonObject { ["door"] = new JsonObject { ["cell"] = door(), ["mode"] = "COMBAT_DOOR_MODE_CLOSE" } }),
            cancellationToken).ConfigureAwait(false);
        if (closed[0]["applied"].AsBool() != true)
        {
            throw new AcceptanceException($"door close refused: {closed[0].ToJson()}");
        }

        after = await LabOps.TickReadNAsync(h, 180, cancellationToken).ConfigureAwait(false);
        if (after.Door(cx, cz - 13))
        {
            throw new AcceptanceException("door hold-open True after close (<nil>)");
        }

        if (after.PhysicalDoor(cx, cz - 13))
        {
            throw new AcceptanceException("door still physically open after clearance and close: True (<nil>)");
        }

        report["doorClosed"] = true;
    }

    private static JsonObject AfterJson(LabState state)
    {
        var pawns = new JsonObject();
        foreach (var (id, row) in state.Pawns.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            pawns[id] = row.DeepClone();
        }

        return new JsonObject { ["pawns"] = pawns, ["doors"] = new JsonArray([.. state.Doors.Select(d => d?.DeepClone())]) };
    }
}
