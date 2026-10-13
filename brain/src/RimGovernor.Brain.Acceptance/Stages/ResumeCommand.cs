using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

// acceptance resume: continue the case a root holds paused at a breakpoint. A run cut by -break leaves its ring with a
// "break" bundle as the next entry and Ring.Break set; resume finds those rings under <root>/checkpoints (or checks the
// cases named) and runs them, which resumes from the bundle the way any ring resume does. stop discards them
// (DiscardBreaks).

/// <summary>A ring holding a breakpoint: the case and its index.</summary>
public sealed record BreakRing(string Name, string Dir, Ring Ring);

/// <summary>What the runner parsed from the run flags a resume was given (Go <c>parseRun</c>'s results).</summary>
/// <param name="Cases">The cases the arguments selected.</param>
/// <param name="Fresh">-fresh.</param>
/// <param name="Repeat">-repeat.</param>
/// <param name="PostmortemOnly">-postmortem-only.</param>
/// <param name="Seed">-seed, "" when none.</param>
/// <param name="Options">The runner's own options object, handed back to <see cref="IResumeRunner.RunCasesAsync"/> untouched.</param>
public sealed record ParsedRun(IReadOnlyList<AcceptanceCase> Cases, bool Fresh, int Repeat, bool PostmortemOnly, string Seed, object Options);

/// <summary>What `acceptance resume` needs from the runner (Go <c>parseRun</c> and <c>runCases</c> of <c>main.go</c>).</summary>
public interface IResumeRunner
{
    /// <summary>Parses the case names and run flags; a usage error throws <see cref="AcceptanceException"/> (exit 2).</summary>
    ParsedRun ParseRun(IReadOnlyList<string> args, TextWriter stderr);

    /// <summary>Runs the cases and returns the process exit code.</summary>
    Task<int> RunCasesAsync(ParsedRun run, TextWriter stdout, CancellationToken cancellationToken);
}

/// <summary>The <c>acceptance resume</c> subcommand.</summary>
public static class ResumeCommand
{
    public const string Usage = """

          acceptance resume [<case>...] -root <dir> [run flags]
            continues the case(s) a -break run left paused in <root> from their break bundles (the game relaunches
            on the bundle); with no case named, the one case paused there; -break again stops at a later point
        """;

    /// <summary>Lists the rings under root/checkpoints whose last run paused at a breakpoint, by case name.</summary>
    public static IReadOnlyList<BreakRing> BreakRings(string root)
    {
        string baseDir = Path.Combine(root, "checkpoints");
        var found = new List<BreakRing>();
        if (!Directory.Exists(baseDir))
        {
            return found;
        }

        foreach (string path in Directory.EnumerateFiles(baseDir, Checkpoints.CheckpointRingFile, SearchOption.AllDirectories))
        {
            string dir = Path.GetDirectoryName(path)!;
            Ring? ring;
            try
            {
                ring = Checkpoints.ReadRing(dir);
            }
            catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (ring?.Break is null)
            {
                continue;
            }

            string rel = Path.GetRelativePath(baseDir, dir).Replace('\\', '/');
            found.Add(new BreakRing(rel, dir, ring));
        }

        return [.. found.OrderBy(f => f.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The subcommand: the cases named (or the root's one paused case) run through the runner, each resuming from its
    /// break bundle.
    /// </summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        IResumeRunner runner,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var names = new List<string>();
        IReadOnlyList<string> flagArgs = [];
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith('-'))
            {
                flagArgs = [.. args.Skip(i)];
                break;
            }

            names.Add(args[i]);
        }

        string root = FlagValue(flagArgs, "root");
        if (root.Length == 0)
        {
            await stderr.WriteLineAsync("-root is required").ConfigureAwait(false);
            return 2;
        }

        IReadOnlyList<BreakRing> paused;
        try
        {
            paused = BreakRings(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 1;
        }

        if (names.Count == 0)
        {
            switch (paused.Count)
            {
                case 0:
                    await stderr.WriteLineAsync($"nothing is paused at a breakpoint under {root} (a run with -break leaves one)").ConfigureAwait(false);
                    return 2;
                case 1:
                    names.Add(paused[0].Name);
                    break;
                default:
                    string list = string.Join(", ", paused.Select(p => $"{p.Name} ({p.Ring.Break!.Spec})"));
                    await stderr.WriteLineAsync($"several cases are paused under {root}; name one: {list}").ConfigureAwait(false);
                    return 2;
            }
        }

        foreach (string name in names)
        {
            if (!paused.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal)))
            {
                await stderr.WriteLineAsync($"{name} is not paused at a breakpoint under {root}").ConfigureAwait(false);
                return 2;
            }
        }

        ParsedRun run;
        try
        {
            run = runner.ParseRun([.. names, .. flagArgs], stderr);
        }
        catch (AcceptanceException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }

        if (run.Fresh || run.Repeat > 1 || run.PostmortemOnly || run.Seed.Length > 0)
        {
            await stderr.WriteLineAsync("resume continues from the break bundle: it takes none of -fresh, -repeat, -seed, -postmortem-only").ConfigureAwait(false);
            return 2;
        }

        foreach (BreakRing p in paused)
        {
            foreach (AcceptanceCase c in run.Cases)
            {
                if (string.Equals(p.Name, c.Name, StringComparison.Ordinal))
                {
                    BreakRecord b = p.Ring.Break!;
                    await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"resume: {c.Name} paused at {b.Spec} ({b.Reason}, tick {b.Tick})")).ConfigureAwait(false);
                }
            }
        }

        return await runner.RunCasesAsync(run, stdout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a flag's value from args before the run parser does, for a decision that precedes parsing; "" when absent.</summary>
    public static string FlagValue(IReadOnlyList<string> args, string name)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(name);
        for (int i = 0; i < args.Count; i++)
        {
            foreach (string prefix in new[] { "-" + name, "--" + name })
            {
                if (string.Equals(args[i], prefix, StringComparison.Ordinal) && i + 1 < args.Count)
                {
                    return args[i + 1];
                }

                if (args[i].StartsWith(prefix + "=", StringComparison.Ordinal))
                {
                    return args[i][(prefix.Length + 1)..];
                }
            }
        }

        return "";
    }

    /// <summary>
    /// Removes the rings under root that hold a breakpoint, so the next plain run of each case starts fresh; it names what
    /// it removed. Failures to remove are joined and thrown after the others were tried. `acceptance stop` calls this.
    /// </summary>
    public static void DiscardBreaks(string root, TextWriter stdout)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        var errors = new List<Exception>();
        foreach (BreakRing p in BreakRings(root))
        {
            try
            {
                Checkpoints.DeleteTree(p.Dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors.Add(e);
                continue;
            }

            stdout.WriteLine($"discarded the breakpoint of {p.Name} ({p.Ring.Break!.Spec})");
        }

        if (errors.Count > 0)
        {
            throw new AggregateException(errors);
        }
    }
}
