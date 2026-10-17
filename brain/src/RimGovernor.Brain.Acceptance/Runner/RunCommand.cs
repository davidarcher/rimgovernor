using System.Diagnostics;
using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// <c>acceptance run</c>:
/// the shared runner over the case registry. Resolves the flags and case names, runs the doctor preflight, then executes
/// the cases in order on one game (each Repeat times, with its repeat summary after the attempts).
/// </summary>
public static class RunCommand
{
    /// <summary>The usage lines for the command's help text.</summary>
    public const string Usage = """
          acceptance run <case>... -root <dir> [-output <dir> -game <id> -headless=false -timeout <d> -budget <d> -stall <d> -series <metrics.jsonl> -no-series -evidence capped|full -no-doctor -no-heal -repeat <n> -seed <s>]
            -repeat <n> runs each case n times and reports the pass rate and seeds (<output>/<case>.repeat.json);
            -seed <s> pins a debug or scenario start's world seed (result.json "world".seed) to reproduce a run
        """;

    /// <summary>
    /// The subcommand with the arguments after <c>run</c>, over <see cref="RunDependencies.Current"/>; returns the process
    /// exit code (2 usage or a refused preflight, 1 a failed case).
    /// </summary>
    public static Task<int> RunAsync(
        IReadOnlyList<string> args,
        CaseRegistry registry,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var deps = RunDependencies.Current;
        if (deps is null)
        {
            return FailWiringAsync(stderr);
        }

        return RunAsync(args, registry, deps, stdout, stderr, cancellationToken);
    }

    /// <summary>The subcommand over explicit dependencies.</summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CaseRegistry registry,
        RunDependencies dependencies,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ParsedRun parsed;
        try
        {
            parsed = Parse(args, registry);
        }
        catch (AcceptanceException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }

        return await RunCasesAsync(parsed, dependencies, stdout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the run subcommand's flags and case names. Flags may follow the case names (the parse stops at the first
    /// non-flag, so the names are split off first). A mistake throws <see cref="UsageException"/>.
    /// </summary>
    public static ParsedRun Parse(IReadOnlyList<string> args, CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(registry);
        var fs = new FlagSet("run");
        var root = fs.String("root", "");
        var output = fs.String("output", "");
        var game = fs.String("game", "rimgovernor-trial");
        var headless = fs.Bool("headless", true);
        var timeout = fs.Duration("timeout", RunOptions.DefaultTimeout);
        var budget = fs.Duration("budget", TimeSpan.Zero);
        var stall = fs.Duration("stall", TimeSpan.Zero);
        var evidence = fs.String("evidence", "");
        var series = fs.String("series", "");
        var noSeries = fs.Bool("no-series", false);
        var noDoctor = fs.Bool("no-doctor", false);
        var noHeal = fs.Bool("no-heal", false);
        var repeat = fs.Int("repeat", 1);
        var seed = fs.String("seed", "");
        var names = fs.ParseAfterPositionals(args);
        if (repeat.Value < 1)
        {
            throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"-repeat must be at least 1: {repeat.Value}"));
        }

        EvidenceMode? mode;
        try
        {
            mode = EvidenceBudget.ParseEvidenceMode(evidence.Value);
        }
        catch (AcceptanceException e)
        {
            throw new UsageException(e.Message, e);
        }

        EvidenceBudget.SetEvidenceMode(mode);
        if (fs.Args.Count > 0)
        {
            throw new UsageException($"case names must precede the flags: [{string.Join(' ', fs.Args)}]");
        }

        if (names.Count == 0)
        {
            throw new UsageException("run needs at least one case name (see `acceptance list`)");
        }

        if (root.Value.Length == 0)
        {
            throw new UsageException("-root is required");
        }

        if (!Path.IsPathFullyQualified(root.Value))
        {
            throw new UsageException($"-root must be absolute: {root.Value}");
        }

        var outputDir = Path.GetFullPath(output.Value.Length == 0 ? Path.Combine(root.Value, "acceptance") : output.Value);
        var options = new RunOptions
        {
            Root = root.Value,
            Output = outputDir,
            GameId = game.Value,
            Headless = headless.Value,
            Timeout = timeout.Value,
            Budget = budget.Value,
            Stall = stall.Value,
            Evidence = mode,
            Series = series.Value.Length == 0 ? "" : Path.GetFullPath(series.Value),
            NoSeries = noSeries.Value,
            NoDoctor = noDoctor.Value,
            NoHeal = noHeal.Value,
            Repeat = repeat.Value,
            Seed = seed.Value,
        };
        var selected = new List<AcceptanceCase>(names.Count);
        foreach (var name in names)
        {
            selected.Add(registry.Lookup(name) ?? throw new UsageException($"unknown case \"{name}\" (see `acceptance list`)"));
        }

        return new ParsedRun(selected, options);
    }

    /// <summary>
    /// Executes the parsed cases in order on one game after the doctor preflight (each Repeat times, with its repeat
    /// summary after the attempts). The exit code is non-zero when any case failed, 2 when the preflight refused the
    /// run. A stale or fixture-less install is healed by the preflight unless -no-heal, and every case's result.json
    /// says so under "healed".
    /// </summary>
    public static async Task<int> RunCasesAsync(ParsedRun run, RunDependencies dependencies, TextWriter stdout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(stdout);
        var opts = run.Options;
        var selected = run.Cases;
        if (!opts.NoDoctor)
        {
            var result = await PreflightPass.RunAsync(selected, opts, stdout, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                return 2;
            }

            opts = opts with { Healed = result.Healed };
        }

        var runner = new CaseRunner(dependencies);
        var exit = 0;
        foreach (var c in selected)
        {
            var attempts = new List<RepeatAttempt>();
            for (var attempt = 1; attempt <= Math.Max(opts.Repeat, 1); attempt++)
            {
                opts = opts with { Attempt = attempt };
                var started = Stopwatch.StartNew();
                var outcome = await runner.ExecuteAsync(c, opts, cancellationToken).ConfigureAwait(false);
                var code = outcome.Code;
                if (code != 0)
                {
                    exit = 1;
                }

                await PrintCaseAsync(stdout, c, opts, outcome, started.Elapsed).ConfigureAwait(false);
                attempts.Add(RepeatAttempt.From(attempt, opts.CaseOutput(c), outcome.Report, code, started.Elapsed));
            }

            if (opts.Repeat > 1)
            {
                opts = opts with { Attempt = 1 };
                var summary = RepeatSummary.Summarize(c.Name, attempts);
                var path = opts.CaseOutput(c) + ".repeat.json";
                try
                {
                    summary.Write(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    await stdout.WriteLineAsync($"\trepeat summary: {e.Message}").ConfigureAwait(false);
                }

                await stdout.WriteLineAsync($"REPEAT\t{c.Name}\t{summary}\t{path}").ConfigureAwait(false);
            }
        }

        return exit;
    }

    // Prints one attempt's verdict line, error, postmortem digest and drift flags.
    private static async Task PrintCaseAsync(TextWriter stdout, AcceptanceCase c, RunOptions opts, CaseOutcome outcome, TimeSpan wall)
    {
        var status = outcome.Code == 0 ? "PASS" : "FAIL";
        var rounded = TimeSpan.FromMilliseconds(Math.Round(wall.TotalMilliseconds));
        await stdout.WriteLineAsync($"{status}\t{c.Name}\t{GoDuration.Format(rounded)}\t{Path.Combine(opts.CaseOutput(c), "result.json")}").ConfigureAwait(false);
        var error = outcome.Report["error"].AsString();
        if (error.Length > 0)
        {
            await stdout.WriteLineAsync($"\t{error}").ConfigureAwait(false);
        }

        // The digest is the first thing anyone reads after a failure; under suite it lands in the case log.
        outcome.Diagnosis?.Write(stdout);
        foreach (var flag in outcome.Drift)
        {
            await stdout.WriteLineAsync($"\tdrift: {flag}").ConfigureAwait(false);
        }
    }

    private static async Task<int> FailWiringAsync(TextWriter stderr)
    {
        await stderr.WriteLineAsync("acceptance run: no RunDependencies.Current wired by the tool host").ConfigureAwait(false);
        return 1;
    }
}

/// <summary>What <see cref="RunCommand.Parse"/> resolved: the cases in order and the options every case runs with.</summary>
public sealed record ParsedRun(IReadOnlyList<AcceptanceCase> Cases, RunOptions Options);
