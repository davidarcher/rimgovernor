using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>tools/baselinegen</c> (Go <c>baselinegen.go</c>): regenerates the shared tribal baseline save through the harness's own start.</summary>
internal static class ToolsBaselineGen
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "tools/baselinegen",
        Tier = CaseTier.Off,
        Scope = "Fixture generation: scenario start " + Baselines.BaselineStart.Scenario + " seed " + Baselines.BaselineStart.Seed
            + " saved as " + Baselines.BaselineName + " for the shared baseline fixture (issue #1).",
        Start = new ScenarioStart { Spec = Baselines.BaselineStart },
        Budget = TimeSpan.FromMinutes(15),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var spec = Baselines.BaselineStart;
        var row = new JsonObject
        {
            ["spec"] = new JsonObject
            {
                ["scenario"] = spec.Scenario,
                ["count"] = spec.Count,
                ["seed"] = spec.Seed,
                ["biome"] = spec.Biome,
                ["save"] = spec.SaveName,
            },
        };
        s.Report["generated"] = row;

        // The Go case passed the file name with ".rws"; PersistSave appends the extension, so the bare save name is used.
        await VariantGen.SaveVariantAsync(s.Harness, s.Config.Root, s.Config.Headless, Baselines.BaselineName, row, cancellationToken).ConfigureAwait(false);

        // Stamp the start the fresh save answers to, so a later run's reuse check sees it as current.
        Baselines.StampBaseline(s.Config.Root);
    }
}
