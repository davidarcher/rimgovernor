using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/stage</c> (Go <c>case.go</c>): the standard fixtures stage, read back and are live fights.</summary>
internal static class CombatLabStage
{
    /// <summary>How long the liveness check lets the staged fight run: 5 game seconds, far enough for walking raiders to close several cells.</summary>
    private const int LiveTicks = 300;

    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/stage",
        Scope = "Combat lab fixtures (#854): lab-open, lab-choke and lab-ranged each stage on a wiped lab with every pawn at its cell, armed as specified, hostiles under an assault lord, a second staging reads back the same digest, combat.geometry's adjacent_to_choke proposes exactly lab-choke's three inner cells behind the gap (#881), and lab-open's raiders close on the colonists within 300 ticks.",
        Start = new LabStart { Colonists = 5 },
        RequiredOps = [DebugGame.LabStartTool, FixtureStage.Tool, CombatFixtures.LabTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = new JsonObject();
        foreach (var name in CombatFixtures.Names)
        {
            var first = await CombatStage.StageAsync(h, name, cancellationToken).ConfigureAwait(false);
            var second = await CombatStage.StageAsync(h, name, cancellationToken).ConfigureAwait(false);
            if (first.Digest.Length == 0 || first.Digest != second.Digest)
            {
                throw new AcceptanceException($"{name}: stagings differ, digest \"{first.Digest}\" then \"{second.Digest}\": {CombatStage.RowDiff(first.Rows, second.Rows)}");
            }

            var row = new JsonObject
            {
                ["digest"] = first.Digest,
                ["colonists"] = first.Colonists().Count,
                ["hostiles"] = first.Hostiles().Count,
            };
            report[name] = row;
            if (name == "lab-open")
            {
                row["live"] = await ClosesInAsync(s, cancellationToken).ConfigureAwait(false);
            }

            if (name == "lab-choke")
            {
                row["adjacentToChoke"] = await ProposeChokeAsync(s, second, cancellationToken).ConfigureAwait(false);
            }
        }

        s.Report["fixtures"] = report;
    }

    // closesIn proves a staged fixture is a live fight, not a tableau: under their assault lord lab-open's melee raiders
    // close on the colonists.
    private static async Task<JsonObject> ClosesInAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var (before, _) = await CombatStage.ReadAsync(h, cancellationToken).ConfigureAwait(false);
        var (after, tick) = await CombatStage.TickAsync(h, LiveTicks, cancellationToken).ConfigureAwait(false);
        var gapAfter = CombatStage.Gap(after);
        var gapBefore = CombatStage.Gap(before);
        var row = new JsonObject { ["gapBefore"] = gapBefore, ["gapAfter"] = gapAfter, ["tick"] = tick };
        return gapAfter < 0 || gapAfter >= gapBefore
            ? throw new AcceptanceException($"lab-open: raiders did not close in {CaseSupport.Tag(LiveTicks)} ticks (squared gap {CaseSupport.Tag(gapBefore)} then {CaseSupport.Tag(gapAfter)})")
            : row;
    }

    // proposeChoke: adjacent_to_choke on lab-choke's gap, our side three cells inside, proposes exactly the three floor
    // cells inside the gap, the one straight behind it first; the walls either side and the cells outside are not
    // proposed.
    private static async Task<JsonObject> ProposeChokeAsync(ISession s, StagedFixture staged, CancellationToken cancellationToken)
    {
        // The riflemen stand at (cx-2, cz) and (cx+2, cz).
        var cx = 0;
        var cz = 0;
        foreach (var p in staged.Fixture.Pawns)
        {
            if (p.Side == CombatFixtures.Colonist && p.Index == 3)
            {
                cx = p.X + 2;
                cz = p.Z;
            }
        }

        var request = Geometry.ProposeAsk(
            s.Identity,
            Geometry.Role("adjacentToChoke", new JsonObject
            {
                ["choke"] = CaseSupport.Cell(cx, cz + CombatFixtures.ChokeHalf),
                ["ourSide"] = CaseSupport.Cell(cx, cz + CombatFixtures.ChokeHalf - 3),
            }),
            staged.Hostiles(),
            "");
        var reply = await Geometry.CallAsync(s.Harness, "combat-geometry-propose-choke", request, cancellationToken).ConfigureAwait(false);
        var g = Geometry.Observed(reply);
        try
        {
            Geometry.Validate(g, request);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"propose adjacent_to_choke {reply["failure"].ToJson()}: {e.Message}", e);
        }

        var proposed = new List<(int X, int Z)>();
        var proposedJson = new JsonArray();
        foreach (var row in g!["proposed"].AsSlice())
        {
            var cell = row.AsMap()?["cell"].AsMap() ?? [];
            var point = ((int)cell["x"].AsNumberOrZero(), (int)cell["z"].AsNumberOrZero());
            proposed.Add(point);
            proposedJson.Add(new JsonArray(point.Item1, point.Item2));
        }

        var result = new JsonObject { ["cells"] = proposedJson, ["ms"] = g["mainThreadMs"].AsNumberOrZero() };
        var inside = cz + CombatFixtures.ChokeHalf - 1;
        var want = new HashSet<(int, int)> { (cx, inside), (cx - 1, inside), (cx + 1, inside) };
        var shown = string.Join(" ", proposed.Select(p => $"[{CaseSupport.Tag(p.X)} {CaseSupport.Tag(p.Z)}]"));
        if (proposed.Count != want.Count || proposed[0] != (cx, inside))
        {
            throw new AcceptanceException($"adjacent_to_choke on the gap at ({CaseSupport.Tag(cx)},{CaseSupport.Tag(cz + CombatFixtures.ChokeHalf)}) proposed [{shown}], want the three cells at z={CaseSupport.Tag(inside)}, x={CaseSupport.Tag(cx)} first");
        }

        foreach (var p in proposed)
        {
            if (!want.Contains(p))
            {
                throw new AcceptanceException($"adjacent_to_choke on the gap at ({CaseSupport.Tag(cx)},{CaseSupport.Tag(cz + CombatFixtures.ChokeHalf)}) proposed [{CaseSupport.Tag(p.X)} {CaseSupport.Tag(p.Z)}] outside the room's inner row");
            }
        }

        return result;
    }
}
