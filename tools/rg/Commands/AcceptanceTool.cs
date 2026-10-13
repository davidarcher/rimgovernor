using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Acceptance.Cases;

namespace RimGovernor.Tools.Commands;

/// <summary>
/// <c>RimGovernor.cmd acceptance ...</c>: the thin entry to the C# acceptance harness (#3087). The harness lives in
/// brain/src/RimGovernor.Brain.Acceptance, the cases in RimGovernor.Brain.Acceptance.Cases; this registers the cases
/// and hands the arguments over.
/// </summary>
internal static class AcceptanceTool
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var registry = new CaseRegistry();
        CaseCatalog.Register(registry);
        return await AcceptanceCommand.RunAsync(args, registry, Console.Out, Console.Error, cancellationToken).ConfigureAwait(false);
    }
}
