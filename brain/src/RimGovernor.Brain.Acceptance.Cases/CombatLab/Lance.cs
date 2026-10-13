using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/shock-lance</c> (Go <c>lance.go</c>).</summary>
internal static class CombatLabLance
{
    private const string ShockLance = "Apparel_PsychicShockLance";

    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/shock-lance",
        Scope = "GiveJobIntent UseItem on Actions/Apply (#1038), a native op contract and vanilla hediff physics no snapshot can prove: on lab-open cut to one club raider, "
            + "colonist 0 wears a psychic shock lance; one Actions/Apply UseItem job of the worn lance on the raider applies, the colonist takes the verb's "
            + "UseVerbOnThingStatic job on the raider, and within 1200 ticks the raider is down with PsychicShock and alive. The planner's choice of target "
            + "and wearer is the policy test (population_lance_test.go).",
        Start = new LabStart { Colonists = 3 },
        RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool, "rimgovernor/operations_apply"],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    // lanceKit keeps one raider and gives colonist 0 a worn shock lance.
    private static void Kit(LabFixture f, int cx, int cz)
    {
        var pawns = new List<LabPawn>();
        var raiders = 0;
        foreach (var p in f.Pawns)
        {
            if (p.Side == CombatFixtures.Hostile && ++raiders > 1)
            {
                continue;
            }

            if (p.Side == CombatFixtures.Colonist && p.Index == 0)
            {
                p.Apparel = ShockLance;
            }

            pawns.Add(p);
        }

        f.Pawns = pawns;
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        var staged = await CombatStage.StageAsync(h, "lab-open", cancellationToken, Kit).ConfigureAwait(false);
        var colonists = staged.Colonists();
        var hostiles = staged.Hostiles();
        if (colonists.Count != 3 || hostiles.Count != 1)
        {
            throw new AcceptanceException($"staged {CaseSupport.Tag(colonists.Count)} colonists and {CaseSupport.Tag(hostiles.Count)} hostiles, want 3 and 1");
        }

        var user = colonists[0];
        var raider = hostiles[0];
        var lance = "";
        foreach (var row in staged.Pawns)
        {
            if (row["id"].AsString() != user)
            {
                continue;
            }

            foreach (var w in row["worn"].AsSlice())
            {
                var item = w.AsMap() ?? [];
                if (item["def"].AsString() == ShockLance)
                {
                    lance = item["id"].AsString();
                }
            }
        }

        if (lance.Length == 0)
        {
            throw new AcceptanceException($"colonist 0 wears no {ShockLance}: {new JsonArray([.. staged.Pawns.Select(p => (JsonNode?)p.DeepClone())]).ToJson()}");
        }

        report["lance"] = lance;
        report["user"] = user;
        report["raider"] = raider;
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "lance-acquire", s.Identity, cancellationToken).ConfigureAwait(false);
        var result = await ApplyIntents.ApplyOneAsync(h, "lance-apply", s.Identity, "lance-1", ApplyIntents.GiveJob(user, "UseItem", lance, raider), cancellationToken).ConfigureAwait(false);
        report["result"] = result.DeepClone();
        if (result["applied"].AsMap() is null)
        {
            throw new AcceptanceException($"use_item not applied: {result.ToJson()}");
        }

        var after = await LabOps.TickReadAsync(h, cancellationToken).ConfigureAwait(false);
        var p0 = after.Row(user);
        if (p0["jobThing"].AsString() != raider || p0["job"].AsString() != "UseVerbOnThingStatic")
        {
            throw new AcceptanceException($"colonist 0 after use_item: want UseVerbOnThingStatic on the raider, got {p0.ToJson()}");
        }

        for (var step = 0; step < 20; step++)
        {
            var (pawns, tick) = await CombatStage.TickAsync(h, 60, cancellationToken).ConfigureAwait(false);
            if (!pawns.TryGetValue(raider, out var p))
            {
                throw new AcceptanceException($"raider {raider} left the lab by tick {CaseSupport.Tag(tick)}");
            }

            if (p.Dead)
            {
                throw new AcceptanceException($"raider died by tick {CaseSupport.Tag(tick)}; the lance must down it alive");
            }

            if (!p.Downed)
            {
                continue;
            }

            var full = await ReadHediffsAsync(h, raider, cancellationToken).ConfigureAwait(false);
            report["downed_tick"] = tick;
            report["hediffs"] = CombatLabCases.Strings(full);
            if (!full.Contains("PsychicShock", StringComparer.Ordinal))
            {
                throw new AcceptanceException($"raider down without PsychicShock: [{string.Join(' ', full)}]");
            }

            return;
        }

        throw new AcceptanceException("raider still standing 1200 ticks after the lance order");
    }

    // readHediffs is one pawn's hediff defs from the lab read.
    private static async Task<List<string>> ReadHediffsAsync(Harness h, string id, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync("lance-hediffs", CombatFixtures.StageTool, new JsonObject { ["action"] = "read" }, cancellationToken).ConfigureAwait(false);
        foreach (var r in reply["pawns"].AsSlice())
        {
            var row = r.AsMap() ?? [];
            if (row["id"].AsString() != id)
            {
                continue;
            }

            return [.. row["hediffs"].AsSlice().Select(d => d.AsString())];
        }

        throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"pawn {id} missing from the lab read"));
    }
}
