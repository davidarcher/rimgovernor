using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/drugs</c> (Go <c>drugs.go</c>).</summary>
internal static class CombatLabDrugs
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/drugs",
        Scope = "Enemy drug facts (#1056), a new read contract: on lab-open with the first raider staged on go-juice (GoJuiceHigh) and the second "
            + "luciferium-addicted (LuciferiumAddiction), one step later the snapshot frame's combat_pawns rows carry go_juice_high on the first, "
            + "luciferium_addicted on the second, and neither on the third raider or any colonist. Colonist 0 carries go-juice and steel; "
            + "the native carried_drugs read lists only GoJuice for it and establishes empty drug inventories for the other colonists.",
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
        var staged = await CombatStage.StageAsync(h, "lab-open", cancellationToken, (f, _, _) =>
        {
            var drugs = new Queue<string>(["GoJuiceHigh", "LuciferiumAddiction"]);
            foreach (var p in f.Pawns)
            {
                if (p.Side == CombatFixtures.Colonist && p.Index == 0)
                {
                    p.Inventory = ["GoJuice", "Steel"];
                }

                if (p.Side == CombatFixtures.Hostile && drugs.Count > 0)
                {
                    p.Hediffs = [drugs.Dequeue()];
                }
            }
        }).ConfigureAwait(false);
        var frames = await CombatFrames.OpenAsync(h, s.Identity, cancellationToken).ConfigureAwait(false);
        var (_, tick) = await CombatStage.TickAsync(h, CombatLabMirror.StepTicks, cancellationToken).ConfigureAwait(false);
        var state = await frames.NextAsync(tick, cancellationToken).ConfigureAwait(false);
        var hostiles = staged.Hostiles();
        var colonists = staged.Colonists();

        // [go_juice_high, luciferium_addicted] per pawn.
        var want = new Dictionary<string, (bool Juice, bool Luci)>(StringComparer.Ordinal)
        {
            [hostiles[0]] = (true, false),
            [hostiles[1]] = (false, true),
            [hostiles[2]] = (false, false),
        };
        foreach (var id in colonists)
        {
            want[id] = (false, false);
        }

        var got = new Dictionary<string, (bool Juice, bool Luci)>(StringComparer.Ordinal);
        var carried = new SortedDictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var row in state.Pawns)
        {
            var id = row["id"].AsString();
            var index = colonists.IndexOf(id);
            if (index >= 0)
            {
                var drugs = row["carriedDrugs"].AsMap() ?? throw new AcceptanceException($"colonist {id} has unknown carried drugs");
                var defs = drugs["defs"].AsSlice().Select(d => d.AsString()).ToList();
                var expected = index == 0 ? new List<string> { "GoJuice" } : [];
                if (!defs.SequenceEqual(expected, StringComparer.Ordinal))
                {
                    throw new AcceptanceException($"colonist {id} carried drugs = [{string.Join(' ', defs)}], want [{string.Join(' ', expected)}]");
                }

                carried[id] = CombatLabCases.Strings(defs);
            }

            if (want.ContainsKey(id))
            {
                got[id] = (row["goJuiceHigh"].AsBool() == true, row["luciferiumAddicted"].AsBool() == true);
            }
        }

        var gotJson = new JsonObject();
        foreach (var (id, g) in got.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            gotJson[id] = new JsonArray(g.Juice, g.Luci);
        }

        var carriedJson = new JsonObject();
        foreach (var (id, defs) in carried)
        {
            carriedJson[id] = defs;
        }

        report["drugs"] = gotJson;
        report["carried_drugs"] = carriedJson;
        foreach (var (id, w) in want.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var ok = got.TryGetValue(id, out var g);
            if (!ok || g != w)
            {
                throw new AcceptanceException(
                    $"combat_pawns {id}: [go_juice_high luciferium_addicted] = [{Flag(g.Juice)} {Flag(g.Luci)}] (row present {Flag(ok)}), want [{Flag(w.Juice)} {Flag(w.Luci)}]");
            }
        }
    }

    private static string Flag(bool value) => value ? "true" : "false";
}
