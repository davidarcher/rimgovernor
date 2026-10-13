using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/rescue</c> (Go <c>rescue.go</c>).</summary>
internal static class CombatLabRescue
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/rescue",
        Scope = "Rescue under fire (#867), a native op contract no snapshot can prove: on lab-open with colonist 2 downed, a bed and a player door behind the line, "
            + "combat.geometry's rescue_path role returns colonist 0's pathfinder route to the downed colonist, every cell scored with a hostile line-of-fire flag that agrees with its lines, and a second (warm) read within the 50 ms main-thread budget (#881); "
            + "one combat.orders call forbids the door, orders colonist 0 to rescue colonist 2 and refuses colonist 1's rescue of a standing raider (cannot_rescue); "
            + "one tick later colonist 0's job is Rescue on colonist 2 and the door reads forbidden; a second call allows it again.",
        Start = new LabStart { Colonists = 3 },
        RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    // rescueKit downs colonist 2 in place and adds a bed and a door behind the line.
    private static void Kit(LabFixture f, int cx, int cz)
    {
        foreach (var p in f.Pawns)
        {
            if (p.Side == CombatFixtures.Colonist && p.Index == 2)
            {
                // A downed pawn drops its weapon: stage it unarmed.
                p.Downed = true;
                p.Weapon = "";
                p.WeaponStuff = "";
            }
        }

        f.Things.Add(new FixtureThing { Def = "Bed", Stuff = "WoodLog", X = cx - 6, Z = cz - 16 });
        f.Things.Add(new FixtureThing { Def = "Door", Stuff = "WoodLog", X = cx, Z = cz - 13 });
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        var cx = 0;
        var cz = 0;
        var staged = await CombatStage.StageAsync(h, "lab-open", cancellationToken, (f, x, z) =>
        {
            Kit(f, x, z);
            cx = x;
            cz = z;
        }).ConfigureAwait(false);
        var colonists = staged.Colonists();
        var hostiles = staged.Hostiles();
        if (colonists.Count != 3 || hostiles.Count != 3)
        {
            throw new AcceptanceException($"staged {CaseSupport.Tag(colonists.Count)} colonists and {CaseSupport.Tag(hostiles.Count)} hostiles, want 3 and 3");
        }

        var downed = staged.Fixture.Pawns.Last(p => p.Side == CombatFixtures.Colonist && p.Index == 2);
        await RescuePathAsync(s, colonists[0], downed, hostiles, cancellationToken).ConfigureAwait(false);
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "combat-rescue-acquire", s.Identity, cancellationToken).ConfigureAwait(false);
        await LabOps.DraftAllAsync(h, s.Identity, "rescue-draft", colonists.GetRange(0, 2), cancellationToken).ConfigureAwait(false);
        JsonObject Door() => CaseSupport.Cell(cx, cz - 13);
        var results = await LabOps.IssueAsync(
            h,
            s.Identity,
            "combat-rescue-1",
            new JsonArray(
                new JsonObject { ["door"] = new JsonObject { ["cell"] = Door(), ["mode"] = "COMBAT_DOOR_MODE_FORBID" } },
                new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[0]), ["rescue"] = new JsonObject { ["downed"] = LabOps.PawnTarget(colonists[2]) } },
                new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[1]), ["rescue"] = new JsonObject { ["downed"] = LabOps.PawnTarget(hostiles[0]) } }),
            cancellationToken).ConfigureAwait(false);
        report["results"] = new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())]);
        if (results[0]["applied"].AsBool() != true)
        {
            throw new AcceptanceException($"door forbid refused: {results[0].ToJson()}");
        }

        if (results[1]["applied"].AsBool() != true || results[1]["jobDef"].AsString() != "Rescue")
        {
            throw new AcceptanceException($"rescue not applied with job Rescue: {results[1].ToJson()}");
        }

        const string cannotRescue = "cannot_rescue";
        if (results[2]["applied"].AsBool() == true || results[2]["refusal"].AsString() != cannotRescue)
        {
            throw new AcceptanceException($"rescue of a standing raider: want {cannotRescue}, got {results[2].ToJson()}");
        }

        var after = await LabOps.TickReadAsync(h, cancellationToken).ConfigureAwait(false);
        var rescuer = after.Row(colonists[0]);
        if (rescuer["job"].AsString() != "Rescue" || rescuer["jobThing"].AsString() != colonists[2])
        {
            throw new AcceptanceException($"colonist 0 after rescue: {rescuer.ToJson()}");
        }

        CheckForbidden(after, cx, cz - 13, true, "forbid");
        var allowed = await LabOps.IssueAsync(
            h,
            s.Identity,
            "combat-rescue-2",
            new JsonArray(new JsonObject { ["door"] = new JsonObject { ["cell"] = Door(), ["mode"] = "COMBAT_DOOR_MODE_ALLOW" } }),
            cancellationToken).ConfigureAwait(false);
        if (allowed[0]["applied"].AsBool() != true)
        {
            throw new AcceptanceException($"door allow refused: {allowed[0].ToJson()}");
        }

        after = await LabOps.TickReadAsync(h, cancellationToken).ConfigureAwait(false);
        CheckForbidden(after, cx, cz - 13, false, "allow");
        report["doorAllowed"] = true;
    }

    private static void CheckForbidden(LabState state, int x, int z, bool want, string when)
    {
        bool forbidden;
        try
        {
            forbidden = state.DoorForbidden(x, z);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"door forbidden {(want ? "false" : "true")} after {when} ({e.Message})", e);
        }

        if (forbidden != want)
        {
            throw new AcceptanceException($"door forbidden {(forbidden ? "true" : "false")} after {when} (<nil>)");
        }
    }

    // rescuePath reads the rescue_path role: a route from the rescuer to the downed colonist, ending next to it, whose
    // every cell's hostile line of fire flag is the OR of its lines.
    private static async Task RescuePathAsync(ISession s, string rescuer, LabPawn downed, IReadOnlyList<string> hostiles, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var request = Geometry.ProposeAsk(
            s.Identity,
            Geometry.Role("rescuePath", new JsonObject { ["to"] = CaseSupport.Cell(downed.X, downed.Z) }),
            hostiles,
            rescuer);

        // Two reads: the first on a fresh game pays one-time costs; the second, warm, must fit the main-thread budget.
        JsonObject? g = null;
        var ms = new List<double>();
        var labels = new[] { "combat-rescue-path", "combat-rescue-path-warm" };
        for (var i = 0; i < labels.Length; i++)
        {
            var reply = await Geometry.CallAsync(s.Harness, labels[i], request, cancellationToken).ConfigureAwait(false);
            g = Geometry.Observed(reply);
            try
            {
                Geometry.Validate(g, request);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"rescue_path read {CaseSupport.Tag(i + 1)} reply {reply.ToJson()}: {e.Message}", e);
            }

            ms.Add(g!["mainThreadMs"].AsNumberOrZero());
        }

        report["rescuePathMs"] = new JsonObject { ["cold"] = ms[0], ["warm"] = ms[1] };
        if (ms[1] > CombatLabMirror.GeometryBudgetMs)
        {
            throw new AcceptanceException(
                $"warm rescue_path read took {ms[1].ToString("0.0", CultureInfo.InvariantCulture)} ms of main thread (cold {ms[0].ToString("0.0", CultureInfo.InvariantCulture)} ms), over {CombatLabMirror.GeometryBudgetMs.ToString("0", CultureInfo.InvariantCulture)} ms");
        }

        var route = g!["proposed"].AsSlice();
        var fire = 0;
        for (var i = 0; i < route.Count; i++)
        {
            var row = route[i].AsMap() ?? [];
            var any = row["lines"].AsSlice().Any(line => line.AsMap()?["lineOfFire"].AsBool() == true);
            if ((row["hostileLineOfFire"].AsBool() == true) != any)
            {
                throw new AcceptanceException($"route cell {CaseSupport.Tag(i)} line-of-fire flag {(row["hostileLineOfFire"].AsBool() == true ? "true" : "false")} disagrees with its lines: {row.ToJson()}");
            }

            if (any)
            {
                fire++;
            }
        }

        report["rescuePath"] = new JsonObject { ["cells"] = route.Count, ["underFire"] = fire, ["mainThreadMs"] = g["mainThreadMs"].AsNumberOrZero() };
        if (route.Count == 0)
        {
            throw new AcceptanceException($"no rescue route to {CaseSupport.Tag(downed.X)},{CaseSupport.Tag(downed.Z)}");
        }

        var last = (route[^1].AsMap() ?? [])["cell"].AsMap() ?? [];
        var lx = (int)last["x"].AsNumberOrZero();
        var lz = (int)last["z"].AsNumberOrZero();
        var dx = lx - downed.X;
        var dz = lz - downed.Z;
        if (dx is < -1 or > 1 || dz is < -1 or > 1)
        {
            throw new AcceptanceException($"rescue route ends at {CaseSupport.Tag(lx)},{CaseSupport.Tag(lz)}, not next to {CaseSupport.Tag(downed.X)},{CaseSupport.Tag(downed.Z)}");
        }
    }
}
