using System.Text;
using RimGovernor.Tools.Commands;
using RimGovernor.Tools.Commands.ColonyReview;
using RimGovernor.Tools.Commands.ThoughtAudit;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools;

/// <summary>
/// The brain tool host: the subcommands that build on brain/ projects (the acceptance harness and its cases, remote
/// acceptance, telemetry readers, the game audits). RimGovernor.cmd reaches it through the repo front door
/// (tools/repo), which publishes it into .rimgovernor/rg/ when its sources changed and forwards the command line;
/// workflows publish it on their own.
/// </summary>
internal static class Program
{
    private const string Usage = """
        usage: rg <tool> [flags] [args]   (from a checkout: RimGovernor.cmd <tool> ...)

        tools:
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
        // Subcommands print UTF-8; no byte-order mark.
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 0 || args[0] is "-h" or "-help" or "--help" or "help")
        {
            Console.Out.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
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
            return tool switch
            {
                "forecastresiduals" => await ForecastResidualsCommand.RunAsync(rest, cts.Token).ConfigureAwait(false),
                "pawnactivity" => await PawnActivityCommand.RunAsync(rest, cts.Token).ConfigureAwait(false),
                "acceptance" => await AcceptanceTool.RunAsync(rest, cts.Token).ConfigureAwait(false),
                "remoteaccept" => await RemoteAcceptTool.RunAsync(rest, cts.Token).ConfigureAwait(false),
                "remotebundle" => await RemoteBundleTool.RunAsync(rest, cts.Token).ConfigureAwait(false),
                "thoughtaudit" => await ThoughtAuditCommand.RunAsync(rest, cts.Token).ConfigureAwait(false),
                "colonyreview" => await ColonyReviewCommand.RunAsync(rest, cts.Token).ConfigureAwait(false),
                "launch-phases" => await LaunchPhasesCommand.RunAsync(rest, cts.Token).ConfigureAwait(false),
                "native-build" => await NativeBuildCommand.RunAsync(rest, cts.Token).ConfigureAwait(false),
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
