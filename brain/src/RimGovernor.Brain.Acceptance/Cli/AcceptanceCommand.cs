using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The <c>acceptance</c> command (Go <c>cmd/acceptance</c>): <c>RimGovernor.cmd acceptance &lt;subcommand&gt;</c>.
/// The tool host passes the populated case registry; the remaining subcommands (run, why, ...) land with slice 4 (#3087).
/// </summary>
public static class AcceptanceCommand
{
    private const string Usage = """
        usage: RimGovernor.cmd acceptance <subcommand> [flags] [args]

          acceptance list [-cost [-baseline <result.json|metrics.jsonl>]] [-tier nightly|smoke] [<case>|<area>/...]...
          acceptance stop -root <dir> [-game <id>]
          acceptance setup ... (acceptance setup -help)
          acceptance doctor -root <dir> [-output <dir> -game <id> -worktree <dir> -heal]
          acceptance prune -output <dir> [-keep n -dry-run]
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
            case "setup":
                return await new AcceptanceSetup().RunAsync(rest, stdout, stderr, cancellationToken).ConfigureAwait(false);
            case "doctor":
                return await AcceptanceDoctor.RunAsync(rest, stdout, stderr, cancellationToken).ConfigureAwait(false);
            case "prune":
                return await AcceptancePrune.RunAsync(rest, stdout, stderr, cancellationToken).ConfigureAwait(false);
            default:
                await stderr.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"unknown subcommand \"{args[0]}\"\n\n{Usage}")).ConfigureAwait(false);
                return 2;
        }
    }

    /// <summary>
    /// Prints the registry, the named cases and areas (<c>&lt;area&gt;/...</c>) or a tier (<c>-tier</c>), one per line
    /// with its scope. <c>-cost</c> adds each case's baseline wall and boot time and a total for the set; without a
    /// baseline row a case is "untimed".
    /// </summary>
    private static async Task<int> ListAsync(IReadOnlyList<string> args, CaseRegistry registry, TextWriter stdout, TextWriter stderr)
    {
        var names = new List<string>();
        bool withCost = false;
        string baseline = "";
        string tier = "";
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i].TrimStart('-');
            if (!args[i].StartsWith('-'))
            {
                names.Add(args[i]);
            }
            else if (arg == "cost")
            {
                withCost = true;
            }
            else if (arg is "baseline" or "tier" && i + 1 < args.Count)
            {
                if (arg == "baseline")
                {
                    baseline = args[++i];
                }
                else
                {
                    tier = args[++i];
                }
            }
            else
            {
                await stderr.WriteLineAsync($"list: unknown flag {args[i]}").ConfigureAwait(false);
                return 2;
            }
        }

        IReadOnlyList<AcceptanceCase> selected;
        try
        {
            if (tier.Length > 0)
            {
                if (names.Count > 0)
                {
                    await stderr.WriteLineAsync("-tier takes no case names").ConfigureAwait(false);
                    return 2;
                }

                selected = Tiers.CasesOf(registry, tier);
            }
            else
            {
                selected = registry.Select(names);
            }
        }
        catch (AcceptanceException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }

        if (!withCost)
        {
            foreach (AcceptanceCase c in selected)
            {
                await stdout.WriteLineAsync($"{c.Name}\t{c.Scope}").ConfigureAwait(false);
            }

            return 0;
        }

        CostTable? table = null;
        if (baseline.Length > 0)
        {
            try
            {
                table = CostTable.Load(baseline);
            }
            catch (AcceptanceException e)
            {
                await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
                return 2;
            }
        }

        await stdout.WriteLineAsync("case\twall\tboot\tscope").ConfigureAwait(false);
        foreach (AcceptanceCase c in selected)
        {
            string wall = "untimed";
            string boot = "";
            if (CostTable.Of(table, c.Name, out CostRow row))
            {
                wall = CostTable.Format(row.Wall);
                boot = CostTable.Format(row.Boot);
            }

            await stdout.WriteLineAsync($"{c.Name}\t{wall}\t{boot}\t{c.Scope}").ConfigureAwait(false);
        }

        CostSummary summary = CostTable.Summarize(table, selected.Select(c => c.Name));
        await stdout.WriteLineAsync(table is null ? $"total: {summary} (no -baseline given)" : $"total: {summary} (baseline {table.Path})").ConfigureAwait(false);
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
            bool stopped = await Game.StopGameAsync(Path.GetFullPath(root), game, timeout.Token).ConfigureAwait(false);
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
