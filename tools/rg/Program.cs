using System.Text;
using RimGovernor.Tools.Commands;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools;

/// <summary>
/// The repo tool host: one console tool, one subcommand per workflow tool. Started by RimGovernor.cmd, which
/// publishes it into .rimgovernor/tools/ and rebuilds when it reports itself stale.
/// </summary>
internal static class Program
{
    private const string Usage = """
        usage: RimGovernor.cmd <tool> [flags] [args]

        tools:
          land      squash a task branch onto local main under the landing lock
          test      run the checks the working tree's changes owe
          issue     print a GitHub issue and its comments as markdown
          affected  list the changed files and the checks they owe
          acceptance  the native acceptance harness (list, stop, ...)

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
                "land" => await LandCommand.RunAsync(rest, cts.Token).ConfigureAwait(false),
                "test" => await TestCommand.RunAsync(rest, cts.Token).ConfigureAwait(false),
                "issue" => await IssueCommand.RunAsync(rest, cts.Token).ConfigureAwait(false),
                "affected" => await Affected.RunAsync(rest, cts.Token).ConfigureAwait(false),
                "acceptance" => await AcceptanceTool.RunAsync(rest, cts.Token).ConfigureAwait(false),
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
