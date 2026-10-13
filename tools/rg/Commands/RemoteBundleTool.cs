using RimGovernor.Brain.RemoteBundle;

namespace RimGovernor.Tools.Commands;

/// <summary>
/// <c>RimGovernor.cmd remotebundle ...</c>: the thin entry to the encrypted remote-acceptance bundle boundary (#3096).
/// The logic lives in brain/src/RimGovernor.Brain.RemoteBundle.
/// </summary>
internal static class RemoteBundleTool
{
    public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken) =>
        RemoteBundleCommand.RunAsync(args, Console.Out, Console.Error, cancellationToken);
}
