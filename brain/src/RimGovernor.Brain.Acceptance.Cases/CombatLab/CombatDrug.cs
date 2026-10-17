using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/combat_drug</c> (Go <c>combat_drug.go</c>).</summary>
internal static class CombatLabCombatDrug
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/combat_drug",
        Scope = "The combat_drug order (#1311), a native op contract no snapshot can prove: on lab-open with colonist 0 carrying go-juice, colonist 1 "
            + "carrying nothing and colonist 2 carrying nothing, all drafted, one combat.orders call orders combat_drug for each; "
            + "colonist 0's applies as Ingest, colonists 1 and 2 refuse no_drug, and 300 ticks later colonist 0 is GoJuiceHigh.",
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
        var staged = await CombatStage.StageAsync(h, "lab-open", cancellationToken, (f, _, _) =>
        {
            foreach (var p in f.Pawns)
            {
                if (p.Side == CombatFixtures.Colonist && p.Index == 0)
                {
                    p.Inventory = ["GoJuice"];
                }
            }
        }).ConfigureAwait(false);
        var colonists = staged.Colonists();
        if (colonists.Count != 3)
        {
            throw new AcceptanceException($"staged {CaseSupport.Tag(colonists.Count)} colonists, want 3");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "combat-drug-acquire", identity, cancellationToken).ConfigureAwait(false);
        await LabOps.DraftAllAsync(h, identity, "draft", colonists, cancellationToken).ConfigureAwait(false);
        var orders = new JsonArray();
        foreach (var id in colonists)
        {
            orders.Add(new JsonObject { ["pawn"] = LabOps.PawnTarget(id), ["combatDrug"] = "GoJuice" });
        }

        var results = await LabOps.IssueAsync(h, identity, "combat-drug", orders, cancellationToken).ConfigureAwait(false);
        var shown = new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())]);
        report["results"] = shown;
        if (results.Count != 3)
        {
            throw new AcceptanceException($"{CaseSupport.Tag(results.Count)} results, want 3: {shown.ToJson()}");
        }

        if (results[0]["applied"].AsBool() != true || results[0]["jobDef"].AsString() != "Ingest")
        {
            throw new AcceptanceException($"colonist 0: want applied Ingest, got {results[0].ToJson()}");
        }

        foreach (var i in new[] { 1, 2 })
        {
            if (results[i]["applied"].AsBool() == true || results[i]["refusal"].AsString() != "no_drug")
            {
                throw new AcceptanceException($"colonist {CaseSupport.Tag(i)}: want refusal no_drug, got {results[i].ToJson()}");
            }
        }

        var after = await LabOps.TickReadNAsync(h, 300, cancellationToken).ConfigureAwait(false);
        var p0 = after.Row(colonists[0]);
        report["after"] = p0.DeepClone();
        if (!p0["hediffs"].AsSlice().Any(v => v.AsString() == "GoJuiceHigh"))
        {
            throw new AcceptanceException($"colonist 0 not GoJuiceHigh 300 ticks after the order: {p0.ToJson()}");
        }
    }
}
