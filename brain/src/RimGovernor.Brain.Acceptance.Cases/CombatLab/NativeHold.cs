using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/native-hold</c> (Go <c>native_hold.go</c>).</summary>
internal static class CombatLabNativeHold
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/native-hold",
        Tier = CaseTier.Nightly,
        Scope = "Go assigns one ranged firing cell; vanilla native execution acquires and retargets without Go micro orders (#2477, #1857). "
            + "A Go snapshot cannot prove vanilla target acquisition, autonomous retargeting, shots or position retention. "
            + "On a pinned lab one defender moves to the cell, holds fire for 600 ticks with live reachable hostiles, then receives hold-position and fire-at-will once. "
            + "Native shot events must name two targets, with a later shot at another live hostile after the first target is observed unavailable through ordinary combat. "
            + "Every observed hold position must equal the assigned cell; no attack, movement or renewed hold orders follow assignment.",
        Start = new LabStart { Colonists = 1 },
        RequiredOps = [DebugGame.LabStartTool, FixtureStage.Tool, CombatFixtures.LabTool],
        QuietWorld = true,
        NoCheckpoint = true,
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
        var identity = s.Identity;
        var report = s.Report;
        var x = 0;
        var z = 0;
        var staged = await CombatStage.StageAsync(h, "lab-ranged", cancellationToken, (f, cx, cz) =>
        {
            x = cx;
            z = cz - 9;
            f.Colonists = 1;
            f.Layout = null;
            f.Pawns =
            [
                new LabPawn { Side = CombatFixtures.Colonist, Index = 0, X = x, Z = z - 2, Weapon = "Gun_ChargeRifle", Apparel = "Apparel_PowerArmor" },
                new LabPawn { Side = CombatFixtures.Hostile, Kind = CombatFixtures.Gunner, X = x - 2, Z = z + 16, Weapon = "Bow_Short" },
                new LabPawn { Side = CombatFixtures.Hostile, Kind = CombatFixtures.Gunner, X = x + 2, Z = z + 20, Weapon = "Bow_Short" },
            ];
        }).ConfigureAwait(false);
        var defender = staged.Colonists()[0];
        var hostiles = new HashSet<string>(staged.Hostiles(), StringComparer.Ordinal);
        var frames = await CombatFrames.OpenAsync(h, identity, cancellationToken).ConfigureAwait(false);
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "native-hold-acquire", identity, cancellationToken).ConfigureAwait(false);
        await LabOps.DraftAllAsync(h, identity, "native-hold-draft", [defender], cancellationToken).ConfigureAwait(false);

        async Task AssignAsync(string label, JsonArray orders)
        {
            var results = await LabOps.IssueAsync(h, identity, label, orders, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < results.Count; i++)
            {
                if (results[i]["applied"].AsBool() != true)
                {
                    throw new AcceptanceException($"{label} order {CaseSupport.Tag(i)} refused: {results[i].ToJson()}");
                }
            }
        }

        await AssignAsync(
            "native-hold-move",
            new JsonArray(
                new JsonObject { ["pawn"] = LabOps.PawnTarget(defender), ["fireMode"] = "COMBAT_FIRE_MODE_HOLD" },
                new JsonObject { ["pawn"] = LabOps.PawnTarget(defender), ["move"] = CaseSupport.Cell(x, z) })).ConfigureAwait(false);
        var arrived = false;
        for (var ticks = 0; ticks < 300; ticks++)
        {
            var (rows, _) = await CombatStage.TickAsync(h, 1, cancellationToken).ConfigureAwait(false);
            if (rows.TryGetValue(defender, out var at) && at.X == x && at.Z == z)
            {
                arrived = true;
                break;
            }
        }

        if (!arrived)
        {
            throw new AcceptanceException($"defender did not reach assigned cell {CaseSupport.Tag(x)},{CaseSupport.Tag(z)}");
        }

        await AssignAsync("native-hold-negative", new JsonArray(new JsonObject { ["pawn"] = LabOps.PawnTarget(defender), ["holdPosition"] = new JsonObject() })).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var first = "";
        long unavailableTick = 0;
        report["defender"] = defender;
        report["assignedCell"] = CaseSupport.Cell(x, z);
        report["positionSampleTicks"] = CombatLabMirror.StepTicks;
        var shotRows = new JsonArray();
        report["shots"] = shotRows.DeepClone();
        for (var elapsed = 0; elapsed < 600 + CombatLabMirror.FightTicks; elapsed += CombatLabMirror.StepTicks)
        {
            if (elapsed == 600)
            {
                // This is the final Go combat assignment. The remainder only advances simulation and reads native
                // outcomes, even if Wait_Combat ends.
                await AssignAsync(
                    "native-hold-engage",
                    new JsonArray(
                        new JsonObject { ["pawn"] = LabOps.PawnTarget(defender), ["holdPosition"] = new JsonObject() },
                        new JsonObject { ["pawn"] = LabOps.PawnTarget(defender), ["fireMode"] = "COMBAT_FIRE_MODE_AT_WILL" })).ConfigureAwait(false);
            }

            var (rows, tick) = await CombatStage.TickAsync(h, CombatLabMirror.StepTicks, cancellationToken).ConfigureAwait(false);
            var exists = rows.TryGetValue(defender, out var p);
            if (!exists || p.Downed || p.Dead || p.X != x || p.Z != z)
            {
                throw new AcceptanceException(
                    $"hold lost at tick {CaseSupport.Tag(tick)}: defender {Describe(p)} (present {(exists ? "true" : "false")}), assigned {CaseSupport.Tag(x)},{CaseSupport.Tag(z)}");
            }

            var state = await frames.NextAsync(tick, cancellationToken).ConfigureAwait(false);
            if (elapsed < 600)
            {
                var eligible = false;
                foreach (var id in hostiles)
                {
                    if (rows.TryGetValue(id, out var target) && !target.Downed && !target.Dead && (((target.X - x) * (target.X - x)) + ((target.Z - z) * (target.Z - z))) <= 24 * 24)
                    {
                        eligible = true;
                    }
                }

                if (!eligible)
                {
                    throw new AcceptanceException($"hold-fire phase has no live ranged target at tick {CaseSupport.Tag(tick)}");
                }
            }

            foreach (var e in state.Events)
            {
                var id = Geometry.EventId(e);
                if (!seen.Add(id))
                {
                    continue;
                }

                if (e["kind"].AsString() != "COMBAT_LOG_KIND_SHOT_FIRED" || e["thingId"].AsString() != defender)
                {
                    continue;
                }

                var eventTick = Geometry.WatermarkTick(e["at"].AsMap());
                if (elapsed < 600)
                {
                    throw new AcceptanceException($"defender fired under hold-fire at tick {eventTick.ToString(CultureInfo.InvariantCulture)}");
                }

                var target = e["targetId"].AsString();
                if (!hostiles.Contains(target))
                {
                    throw new AcceptanceException($"defender shot unexpected target \"{target}\"");
                }

                shotRows.Add(new JsonObject { ["tick"] = eventTick, ["target"] = target });
                report["shots"] = shotRows.DeepClone();
                if (first.Length == 0)
                {
                    first = target;
                    report["firstTarget"] = first;
                }

                var live = rows.TryGetValue(target, out var other);
                if (unavailableTick > 0 && target != first && eventTick > unavailableTick && live && !other.Downed && !other.Dead)
                {
                    report["retargetTick"] = eventTick;
                    report["heldFireTicks"] = 600;
                    return;
                }
            }

            if (first.Length > 0 && unavailableTick == 0)
            {
                var present = rows.TryGetValue(first, out var target);
                if (!present || target.Downed || target.Dead)
                {
                    unavailableTick = tick;
                    report["firstUnavailable"] = new JsonObject
                    {
                        ["tick"] = tick,
                        ["present"] = present,
                        ["downed"] = present && target.Downed,
                        ["dead"] = present && target.Dead,
                    };
                }
            }
        }

        throw new AcceptanceException(
            $"native hold did not shoot a different live hostile after first target became unavailable in {CaseSupport.Tag(CombatLabMirror.FightTicks)} ticks (first \"{first}\", unavailable {unavailableTick.ToString(CultureInfo.InvariantCulture)}, shots {shotRows.ToJson()})");
    }

    private static string Describe(LabPosition p) =>
        string.Create(CultureInfo.InvariantCulture, $"{{Id:{p.Id} Side:{p.Side} X:{p.X} Z:{p.Z} Downed:{(p.Downed ? "true" : "false")} Dead:{(p.Dead ? "true" : "false")}}}");
}
