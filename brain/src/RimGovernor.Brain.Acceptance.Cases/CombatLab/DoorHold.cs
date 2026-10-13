using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/door-hold</c> (Go <c>door_hold.go</c>).</summary>
internal static class CombatLabDoorHold
{
    // The door-hold room: a granite ring around the lab-manhunter colonist and huskies, one door in its north wall facing the wargs.
    private const int HalfX = 3;
    private const int HalfZ = 2;

    // doorHoldTicks is how long the forbidden door must hold, in steps of doorHoldStep; the wargs reach the room inside it.
    private const int HoldTicks = 600;
    private const int HoldStep = 60;

    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/door-hold",
        Scope = "A forbidden door holds colony animals (#1155, epic #929): lab-manhunter with its colonist and both huskies walled in a granite room whose one door faces the wargs; "
            + "one combat.orders call forbids the door and zones both huskies onto a cell outside it (both applied). For 600 ticks, while the wargs are on the map, "
            + "both huskies stay inside the room and the door reads forbidden; a second call allows the door and a husky then walks out to its area within 600 ticks.",
        Start = new LabStart { Colonists = 1 },
        RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool],

        // The wargs are manhunters pressing on the room; a quiet world would freeze them (AcceptanceWorld.WildAnimal) and the hold would prove nothing.
        QuietWorld = false,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    // doorHoldKit walls the fixture centre in and puts the door at (cx, cz+halfZ).
    private static void Kit(LabFixture f, int cx, int cz)
    {
        for (var x = cx - HalfX; x <= cx + HalfX; x++)
        {
            for (var z = cz - HalfZ; z <= cz + HalfZ; z++)
            {
                var edge = x == cx - HalfX || x == cx + HalfX || z == cz - HalfZ || z == cz + HalfZ;
                if (x == cx && z == cz + HalfZ)
                {
                    f.Things.Add(new FixtureThing { Def = "Door", Stuff = "Steel", X = x, Z = z });
                }
                else if (edge)
                {
                    f.Things.Add(new FixtureThing { Def = "Wall", Stuff = "BlocksGranite", X = x, Z = z });
                }
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
        var staged = await CombatStage.StageAsync(h, "lab-manhunter", cancellationToken, (f, x, z) =>
        {
            Kit(f, x, z);
            cx = x;
            cz = z;
        }).ConfigureAwait(false);
        var animals = staged.Animals();
        var wargs = staged.Manhunters();
        if (animals.Count != 2 || wargs.Count != 2)
        {
            throw new AcceptanceException($"staged {CaseSupport.Tag(animals.Count)} animals and {CaseSupport.Tag(wargs.Count)} manhunters, want 2 and 2");
        }

        bool Inside(JsonObject p)
        {
            var x = (int)p["x"].AsNumber();
            var z = (int)p["z"].AsNumber();
            return x > cx - HalfX && x < cx + HalfX && z > cz - HalfZ && z < cz + HalfZ;
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "combat-door-hold-acquire", identity, cancellationToken).ConfigureAwait(false);

        // Areas first: a forbidden door makes the outside cell unreachable, and the area order is refused for it.
        var orders = new JsonArray();
        foreach (var a in animals)
        {
            orders.Add(new JsonObject { ["pawn"] = LabOps.PawnTarget(a), ["animalArea"] = new JsonObject { ["cell"] = CaseSupport.Cell(cx, cz + HalfZ + 3) } });
        }

        orders.Add(new JsonObject { ["door"] = new JsonObject { ["cell"] = CaseSupport.Cell(cx, cz + HalfZ), ["mode"] = "COMBAT_DOOR_MODE_FORBID" } });
        var results = await LabOps.IssueAsync(h, identity, "combat-door-hold-1", orders, cancellationToken).ConfigureAwait(false);
        report["results"] = new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())]);
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i]["applied"].AsBool() != true)
            {
                throw new AcceptanceException($"order {CaseSupport.Tag(i)} refused: {results[i].ToJson()}");
            }
        }

        for (var t = HoldStep; t <= HoldTicks; t += HoldStep)
        {
            var st = await LabOps.TickReadNAsync(h, HoldStep, cancellationToken).ConfigureAwait(false);
            bool forbidden;
            try
            {
                forbidden = st.DoorForbidden(cx, cz + HalfZ);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"tick +{CaseSupport.Tag(t)}: door forbidden false ({e.Message})", e);
            }

            if (!forbidden)
            {
                throw new AcceptanceException($"tick +{CaseSupport.Tag(t)}: door forbidden false (<nil>)");
            }

            var present = 0;
            foreach (var w in wargs)
            {
                if (st.Pawn(w) is { } p)
                {
                    if (p["dead"].AsBool() == true)
                    {
                        continue;
                    }

                    present++;
                }
            }

            if (present == 0)
            {
                throw new AcceptanceException($"tick +{CaseSupport.Tag(t)}: no warg left on the map, the hold proves nothing");
            }

            foreach (var a in animals)
            {
                var p = st.Row(a);
                if (!Inside(p))
                {
                    throw new AcceptanceException($"tick +{CaseSupport.Tag(t)}: husky {a} left the room through the forbidden door: {p.ToJson()}");
                }
            }

            report["held"] = t;
        }

        var allowed = await LabOps.IssueAsync(
            h,
            identity,
            "combat-door-hold-2",
            new JsonArray(new JsonObject { ["door"] = new JsonObject { ["cell"] = CaseSupport.Cell(cx, cz + HalfZ), ["mode"] = "COMBAT_DOOR_MODE_ALLOW" } }),
            cancellationToken).ConfigureAwait(false);
        if (allowed[0]["applied"].AsBool() != true)
        {
            throw new AcceptanceException($"door allow refused: {allowed[0].ToJson()}");
        }

        // The control: with the door allowed the zoned huskies leave, so the hold was the door's.
        for (var t = HoldStep; t <= HoldTicks; t += HoldStep)
        {
            var st = await LabOps.TickReadNAsync(h, HoldStep, cancellationToken).ConfigureAwait(false);
            foreach (var a in animals)
            {
                if (st.Pawn(a) is { } p && !Inside(p))
                {
                    report["leftAfterAllow"] = new JsonObject { ["pawn"] = a, ["ticks"] = t };
                    return;
                }
            }
        }

        throw new AcceptanceException($"no husky left the room within {CaseSupport.Tag(HoldTicks)} ticks of the door being allowed");
    }
}
