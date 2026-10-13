using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>tools/variantsavegen-*</c> (Go <c>variantsavegen.go</c>): one case per sustained-matrix variant, generating its save
/// through the production new-colony operation. Each run overwrites its variant under root/profile/Saves for
/// sustained/matrix-* to load. Map generation does not verify the intended stressor, such as scarce wood; inspect colony
/// facts before trusting a new variant.
/// </summary>
internal static class ToolsVariantSaveGen
{
    internal static IEnumerable<AcceptanceCase> Cases()
    {
        foreach (var v in SustainedMatrix.Variants())
        {
            yield return new AcceptanceCase
            {
                Name = "tools/variantsavegen-" + SustainedMatrix.Short(v.Save),
                Scope = string.Create(CultureInfo.InvariantCulture, $"Fixture generation: scenario start {v.Scenario} seed {v.Seed} saved as {v.Save} for the sustained matrix (issue #1)."),
                Start = new ScenarioStart { Spec = v.Start().Spec },
                Budget = TimeSpan.FromMinutes(8),
                Crew = new Crew { Size = 3 },
                Run = (s, ct) => RunAsync(s, v, ct),
            };
        }
    }

    private static async Task RunAsync(ISession s, Variant v, CancellationToken cancellationToken)
    {
        var row = new JsonObject { ["variant"] = v.ToJson() };
        s.Report["generated"] = row;
        await VariantGen.SaveVariantAsync(s.Harness, s.Config.Root, s.Config.Headless, v.Save, row, cancellationToken).ConfigureAwait(false);
    }
}
