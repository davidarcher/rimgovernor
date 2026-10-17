using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/seeded-staging</c> (Go <c>seeded_staging.go</c>).</summary>
internal static class CombatLabSeededStaging
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/seeded-staging",
        Scope = "Seeded lab staging (#2833): lab-ranged staged twice under seed 7 reads back the same digest and echoes the seed, seed 8 reads a different digest (hostile skills vary), and seed 0 reads the unseeded staging's digest.",
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
        const string name = "lab-ranged";
        var plain = await CombatStage.StageAsync(h, name, cancellationToken).ConfigureAwait(false);
        var zero = await CombatStage.StageAsync(h, name, cancellationToken, CombatStage.WithSeed(0)).ConfigureAwait(false);
        var a = await CombatStage.StageAsync(h, name, cancellationToken, CombatStage.WithSeed(7)).ConfigureAwait(false);
        var b = await CombatStage.StageAsync(h, name, cancellationToken, CombatStage.WithSeed(7)).ConfigureAwait(false);
        var c = await CombatStage.StageAsync(h, name, cancellationToken, CombatStage.WithSeed(8)).ConfigureAwait(false);
        if (zero.Digest != plain.Digest)
        {
            throw new AcceptanceException($"seed 0 digest {zero.Digest} differs from the unseeded {plain.Digest}: {CombatStage.RowDiff(plain.Rows, zero.Rows)}");
        }

        if (a.Seed != 7 || c.Seed != 8 || zero.Seed != 0)
        {
            throw new AcceptanceException($"seeds echoed {CaseSupport.Tag(a.Seed)}, {CaseSupport.Tag(c.Seed)}, {CaseSupport.Tag(zero.Seed)}, want 7, 8, 0");
        }

        if (a.Digest != b.Digest)
        {
            throw new AcceptanceException($"seed 7 stagings differ: {CombatStage.RowDiff(a.Rows, b.Rows)}");
        }

        if (a.Digest == c.Digest)
        {
            throw new AcceptanceException($"seeds 7 and 8 read the same digest {a.Digest}");
        }

        s.Report["digests"] = new JsonObject { ["unseeded"] = plain.Digest, ["seed0"] = zero.Digest, ["seed7"] = a.Digest, ["seed8"] = c.Digest };
    }
}
