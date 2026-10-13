using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/reseed</c> (Go <c>reseed.go</c>).</summary>
internal static class CombatLabReseed
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/reseed",
        Scope = "Post-load reseed (#2849): reseeding the game RNG under seed 7 reads the same next draws every time, even after ticks advanced the RNG between, and seed 8 reads different draws. The same-checkpoint-twice property is also visible across combatlab/metrics-* runs: pass -run-seed and compare the report's run_seed_draws and staged_digest.",
        Start = new LabStart { Colonists = 5 },
        RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var a = await CombatStage.ReseedAsync(h, 7, cancellationToken).ConfigureAwait(false);
        _ = await h.CallAsync("reseed-advance", CombatFixtures.StageTool, new JsonObject { ["action"] = "tick", ["ticks"] = 50 }, cancellationToken).ConfigureAwait(false);
        var b = await CombatStage.ReseedAsync(h, 7, cancellationToken).ConfigureAwait(false);
        var c = await CombatStage.ReseedAsync(h, 8, cancellationToken).ConfigureAwait(false);
        if (!a.SequenceEqual(b))
        {
            throw new AcceptanceException($"seed 7 read draws [{string.Join(" ", a)}] then [{string.Join(" ", b)}]");
        }

        if (a.SequenceEqual(c))
        {
            throw new AcceptanceException($"seeds 7 and 8 read the same draws [{string.Join(" ", a)}]");
        }

        s.Report["draws"] = new JsonObject { ["seed7"] = Draws(a), ["seed7_again"] = Draws(b), ["seed8"] = Draws(c) };
    }

    private static JsonArray Draws(List<int> draws) => new([.. draws.Select(d => (JsonNode?)JsonValue.Create(d))]);
}
