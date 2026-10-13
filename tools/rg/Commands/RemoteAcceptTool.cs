using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Acceptance.Cases;
using RimGovernor.Brain.RemoteAccept;

namespace RimGovernor.Tools.Commands;

/// <summary>
/// <c>RimGovernor.cmd remoteaccept ...</c>: the thin entry to the remote acceptance evidence graph (#3096). The logic
/// lives in brain/src/RimGovernor.Brain.RemoteAccept; this registers the trusted cases <c>progress -prepare</c> maps
/// selection names to output paths with, and hands over the arguments and the credential on stdin.
/// </summary>
internal static class RemoteAcceptTool
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var registry = new CaseRegistry();
        CaseCatalog.Register(registry);
        using var stdin = Console.OpenStandardInput();
        return await RemoteAcceptCommand.RunAsync(args, registry, stdin, Console.Out, Console.Error, cancellationToken).ConfigureAwait(false);
    }
}
