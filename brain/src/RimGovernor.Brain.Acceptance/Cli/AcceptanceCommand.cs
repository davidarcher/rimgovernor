using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The <c>acceptance</c> command (Go <c>cmd/acceptance</c>): <c>RimGovernor.cmd acceptance &lt;subcommand&gt;</c>.
/// The tool host passes the populated case registry; subcommands land slice by slice (#3087).
/// </summary>
public static class AcceptanceCommand
{
    private const string Usage = """
        usage: RimGovernor.cmd acceptance <subcommand> [flags] [args]

          acceptance list [<case>|<area>/...]...
          acceptance stop -root <dir> [-game <id>]
        """;

    /// <summary>Runs one subcommand and returns the process exit code (2 usage, 1 failure).</summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CaseRegistry registry,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (args.Count == 0 || args[0] is "-h" or "-help" or "--help" or "help")
        {
            await (args.Count == 0 ? stderr : stdout).WriteLineAsync(Usage).ConfigureAwait(false);
            return args.Count == 0 ? 2 : 0;
        }

        IReadOnlyList<string> rest = [.. args.Skip(1)];
        switch (args[0])
        {
            case "list":
                return await ListAsync(rest, registry, stdout, stderr).ConfigureAwait(false);
            case "stop":
                return await StopAsync(rest, stdout, stderr, cancellationToken).ConfigureAwait(false);
            default:
                await stderr.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"unknown subcommand \"{args[0]}\"\n\n{Usage}")).ConfigureAwait(false);
                return 2;
        }
    }

    /// <summary>
    /// Prints the registry, or the named cases and areas (<c>&lt;area&gt;/...</c>), one per line with its scope.
    /// <c>-cost</c> and <c>-tier</c> arrive with the cost and tier tables.
    /// </summary>
    private static async Task<int> ListAsync(IReadOnlyList<string> args, CaseRegistry registry, TextWriter stdout, TextWriter stderr)
    {
        var names = new List<string>();
        foreach (string arg in args)
        {
            if (arg.StartsWith('-'))
            {
                await stderr.WriteLineAsync($"list: flag {arg} is not ported yet (-cost and -tier arrive with the cost and tier tables)").ConfigureAwait(false);
                return 2;
            }

            names.Add(arg);
        }

        IReadOnlyList<AcceptanceCase> selected;
        try
        {
            selected = registry.Select(names);
        }
        catch (AcceptanceException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }

        foreach (AcceptanceCase c in selected)
        {
            await stdout.WriteLineAsync($"{c.Name}\t{c.Scope}").ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// Ends the game a root keeps between runs: kills the recorded process tree. A breakpoint the root holds paused is
    /// discarded first, whether or not the game is still up.
    /// </summary>
    private static async Task<int> StopAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        string root = "";
        string game = "rimgovernor-trial";
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "-root" or "--root" when i + 1 < args.Count:
                    root = args[++i];
                    break;
                case "-game" or "--game" when i + 1 < args.Count:
                    game = args[++i];
                    break;
                default:
                    await stderr.WriteLineAsync($"stop: unexpected argument {args[i]}").ConfigureAwait(false);
                    return 2;
            }
        }

        if (root.Length == 0)
        {
            await stderr.WriteLineAsync("-root is required").ConfigureAwait(false);
            return 2;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            bool stopped = await GameProcess.StopAsync(Path.GetFullPath(root), game, timeout.Token).ConfigureAwait(false);
            await stdout.WriteLineAsync(stopped ? $"stopped {game}" : $"nothing running for {game}").ConfigureAwait(false);
            return 0;
        }
        catch (AcceptanceException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 1;
        }
    }
}
