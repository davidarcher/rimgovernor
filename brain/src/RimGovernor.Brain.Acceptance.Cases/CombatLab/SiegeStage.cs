using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/siege-stage</c> (Go <c>siege_stage.go</c>).</summary>
internal static class CombatLabSiegeStage
{
    // siegeBuildTicks bounds the wait for a builder on a frame: the supply drop pods land, the raiders haul them to the
    // blueprints and start a frame, a few thousand ticks on the vanilla camp.
    private const int BuildTicks = 10000;
    private const int StepTicks = 250;

    // siegeFarCells is how far, in cells from the nearest colonist, a camped besieger counts as far away: the lab camp is
    // ~32 cells out, past the 20-cell hostile watch.
    private const int FarCells = 25;

    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/siege-stage",
        Scope = "The stage spec's siege raid (#1147) and the siege fight's native facts (#1154): lab-siege stages its raiders under a real LordJob_Siege at the camp spot; within "
            + "10,000 ticks the lord is in LordToil_Siege and at least one raider works a FinishFrame job on a camp frame. While camped, the combat frame's "
            + "emergency census lists an engaging besieger at least 25 cells from every colonist (the fight is open while the camp is far away), and a drafted "
            + "colonist's attack order on the builder's frame applies as AttackStatic on that frame.",
        Start = new LabStart { Colonists = 3 },
        RequiredOps = [DebugGame.LabStartTool, FixtureStage.Tool, CombatFixtures.LabTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        await RunWindowsAsync(s, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunWindowsAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        var frames = await CombatFrames.OpenAsync(h, s.Identity, cancellationToken).ConfigureAwait(false);
        var staged = await CombatStage.StageAsync(h, "lab-siege", cancellationToken).ConfigureAwait(false);
        var hostiles = staged.Hostiles();
        JsonObject? last = null;
        var far = false;
        for (var waited = 0; waited < BuildTicks; waited += StepTicks)
        {
            var state = await LabOps.TickReadNAsync(h, StepTicks, cancellationToken).ConfigureAwait(false);
            var camped = false;
            foreach (var id in hostiles)
            {
                var p = state.Pawn(id);
                if (p is null)
                {
                    continue;
                }

                last = p;
                if (p["lordJob"].AsString() != "LordJob_Siege")
                {
                    throw new AcceptanceException($"raider {id} left the siege lord: {p.ToJson()}");
                }

                camped = camped || p["lordToil"].AsString() == "LordToil_Siege";
            }

            if (camped && !far)
            {
                far = await FarOpenAsync(frames, hostiles, report, cancellationToken).ConfigureAwait(false);
            }

            foreach (var id in hostiles)
            {
                var p = state.Pawn(id);
                if (p is null || p["lordToil"].AsString() != "LordToil_Siege" || p["job"].AsString() != "FinishFrame")
                {
                    continue;
                }

                var frame = p["jobThing"].AsString();
                report["builder"] = id;
                report["frame"] = frame;
                report["ticks"] = waited + StepTicks;
                if (!far)
                {
                    throw new AcceptanceException($"a raider builds {frame} but no combat frame listed an engaging besieger {CaseSupport.Tag(FarCells)}+ cells out while camped");
                }

                await AttackFrameAsync(s, staged.Colonists()[0], frame, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        throw new AcceptanceException($"no raider in LordToil_Siege on a FinishFrame job within {CaseSupport.Tag(BuildTicks)} ticks (last {(last is null ? "map[]" : last.ToJson())})");
    }

    // siegeFarOpen reads the next combat frame and reports whether its emergency census lists an engaging besieger at
    // least siegeFarCells from the nearest colonist: the threat the fight opens on.
    private static async Task<bool> FarOpenAsync(CombatFrames frames, IReadOnlyList<string> hostiles, Report report, CancellationToken cancellationToken)
    {
        var v = await frames.SnapshotAsync(0, cancellationToken).ConfigureAwait(false);
        var threats = v["emergency"].AsMap()?["threats"].AsMap()?["pawns"].AsSlice() ?? [];
        foreach (var node in threats)
        {
            var t = node.AsMap() ?? [];
            var id = t["pawn"].RefId();

            // The census classes a pawn a Hostile threat when it is a manhunter, of a hostile faction, or breaking out
            // of prison; it is engaging unless the native marks it passive.
            var hostile = t["mentalState"].AsString().Contains("manhunter", StringComparison.OrdinalIgnoreCase)
                || t["factionHostile"].AsBool() == true
                || t["prisonBreak"].AsBool() == true;
            var engaging = t["passive"].AsBool() != true;
            if (!hostile || !engaging || !hostiles.Contains(id, StringComparer.Ordinal) || t["nearestColonistDistance"] is null)
            {
                continue;
            }

            var d = t["nearestColonistDistance"].AsNumber();
            if (d >= FarCells)
            {
                report["farThreat"] = new JsonObject { ["id"] = id, ["distance"] = d, ["tick"] = v["context"].AsMap()?["tick"]?.DeepClone() };
                return true;
            }
        }

        return false;
    }

    // attackFrame drafts colonist and orders it to attack the besiegers' frame: the order applies as AttackStatic and, a
    // tick later, the colonist's job targets the frame.
    private static async Task AttackFrameAsync(ISession s, string colonist, string frame, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var report = s.Report;
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "siege-frame-acquire", identity, cancellationToken).ConfigureAwait(false);
        await LabOps.DraftAllAsync(h, identity, "siege-draft", [colonist], cancellationToken).ConfigureAwait(false);
        var results = await LabOps.IssueAsync(
            h,
            identity,
            "siege-frame-attack",
            new JsonArray(new JsonObject { ["pawn"] = LabOps.PawnTarget(colonist), ["attack"] = LabOps.PawnTarget(frame) }),
            cancellationToken).ConfigureAwait(false);
        report["frameAttack"] = new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())]);
        if (results[0]["applied"].AsBool() != true || results[0]["jobDef"].AsString() != "AttackStatic")
        {
            throw new AcceptanceException($"attack on frame {frame}: want applied AttackStatic, got {results[0].ToJson()}");
        }

        var after = await LabOps.TickReadAsync(h, cancellationToken).ConfigureAwait(false);
        var p = after.Row(colonist);
        if (p["job"].AsString() != "AttackStatic" || p["jobThing"].AsString() != frame)
        {
            throw new AcceptanceException($"colonist {colonist} after the frame attack: {p.ToJson()}");
        }
    }
}
