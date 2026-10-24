using System.Text;
using RimGovernor.Tools.Common;
using RimGovernor.Tools.Workflow.Commands;

namespace RimGovernor.Tools.Workflow;

/// <summary>
/// The repo front door: the workflow tools (land, test, issue, affected) run here; the tools built on brain/ projects
/// are forwarded to the brain tool host (<see cref="BrainHost"/>). Started by RimGovernor.cmd, which publishes it into
/// .rimgovernor/tools/ and rebuilds when it reports itself stale.
/// </summary>
internal static class Program
{
    private const string Usage = """
        usage: RimGovernor.cmd <tool> [flags] [args]

        tools:
          land      squash a task branch onto origin's main and push it (no checks; the nightly validates)
          test      build and test what the working tree's changes owe
          issue     print a GitHub issue and its comments as markdown
          affected  list the changed files and the checks they owe
          launcher-stale  exit 75 when RimGovernorLauncher.exe is older than its sources, 0 when current
                    (-locked: under the launcher build lock; clears torn ref assemblies before the publish)

        brain tools (tools/rg, built on first use after a brain/ change):
          acceptance  the native acceptance harness (list, stop, ...)
          remoteaccept  remote acceptance evidence: export, aggregate, import, index, soak, progress
          remotebundle  encrypted remote-acceptance bundles: pack, publish, verify, extract, bootstrap
          thoughtaudit  regenerate the thought-to-trigger table from the installed game
          colonyreview  render a colony review recording into an HTML report or a site index
          pawnactivity  report pawn time and gaps from the OTLP ring
          forecastresiduals  compare forecast evidence from the OTLP ring
          launch-phases  print a run's launch waterfall (or two runs' per-phase delta) from its traces
          native-build  build the native mod package from this checkout

        <tool> -help shows a tool's flags.
        """;

    private static async Task<int> Main(string[] args)
    {
        // Subcommands print UTF-8 (issue text carries non-ASCII); no byte-order mark.
        Console.OutputEncoding = new UTF8Encoding(false);
        if (SelfBuild.IsStale())
        {
            return SelfBuild.StaleExitCode;
        }
        if (args.Length == 0 || args[0] is "-h" or "-help" or "--help" or "help")
        {
            Console.Out.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        if (args[0] == "--check-stale")
        {
            return 0; // reaching here means not stale
        }
        if (args[0] == "launcher-stale")
        {
            return SelfBuild.LauncherStale(locked: args.Contains("-locked"));
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        var tool = args[0];
        var rest = args[1..];
        try
        {
            if (BrainHost.Tools.Contains(tool))
            {
                return await BrainHost.RunAsync(args, cts.Token).ConfigureAwait(false);
            }
            return tool switch
            {
                "land" => await LandCommand.RunAsync(rest, cts.Token).ConfigureAwait(false),
                "test" => await TestCommand.RunAsync(rest, cts.Token).ConfigureAwait(false),
                "issue" => await IssueCommand.RunAsync(rest, cts.Token).ConfigureAwait(false),
                "affected" => await Affected.RunAsync(rest, cts.Token).ConfigureAwait(false),
                _ => throw new UsageException($"unknown tool \"{tool}\"\n\n{Usage}"),
            };
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        catch (ToolException e)
        {
            Log.Error(tool, e.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Log.Error(tool, "interrupted");
            return 130;
        }
    }
}
