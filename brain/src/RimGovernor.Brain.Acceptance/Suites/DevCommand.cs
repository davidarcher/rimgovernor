using System.Diagnostics;
using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>What <see cref="DevCommand.Parse"/> resolved: the case and the run options of every iteration (Go <c>devOptions</c>).</summary>
public sealed record DevOptions(AcceptanceCase Case, RunOptions Options);

/// <summary>
/// <c>acceptance dev</c> (Go <c>cmd/acceptance/dev.go</c>): the edit loop over one serve-driven case from a checkpoint
/// bundle. The expensive part of iterating on planner or policy code is replaying the colony to the state under test, so
/// each iteration reloads the bundle's save on the kept process (its store and journal restored beside it, never a fresh
/// store) and runs the case's Run and Postmortem as a resumed run, then waits for Enter and goes again. The ring is read
/// and never written, no stage bundle is captured and no series row is appended; every iteration's result.json carries
/// resumed_from and "dev", which the landing gate refuses, since everything before the bundle ran under the code that
/// captured it.
/// <para>
/// Changed from Go: there is no per-iteration <c>go build</c> of the brain binary and no <c>-watch</c>/<c>-module</c>. The
/// brain now lives in the installed mod, so a brain change needs the mod rebuilt and the game restarted
/// (<c>acceptance setup</c>, then a new <c>dev</c> loop); an iteration here re-runs the case's tool-side code only.
/// </para>
/// </summary>
public static class DevCommand
{
    /// <summary>The usage lines for the command's help text.</summary>
    public const string Usage = """
          acceptance dev <area>/<case> -root <dir> [-from <label|dir> -output <dir> -game <id> -headless=false -timeout <d> -budget <d> -stall <d> -evidence capped|full -no-doctor -no-heal]
            reloads the bundle (-from; default the ring's next entry, then its failed bundle) on the kept
            process and runs the case's Run and Postmortem from there, then waits for Enter (q quits);
            each iteration writes <output>/dev/<n>/<case> and is refused by the landing gate
        """;

    /// <summary>
    /// The subcommand: preflight once, then run and wait until the trigger says quit (or stdin ends). The exit code is the
    /// last iteration's (2 on a refused invocation or preflight).
    /// </summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CaseRegistry registry,
        RunDependencies dependencies,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        DevOptions d;
        try
        {
            d = Parse(args, registry);
        }
        catch (AcceptanceException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }

        return await LoopAsync(d, dependencies, stdin, stdout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves the dev subcommand's case name and flags (Go <c>parseDev</c>); throws <see cref="UsageException"/> on a refused invocation.</summary>
    public static DevOptions Parse(IReadOnlyList<string> args, CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(registry);
        var fs = new FlagSet("dev");
        var root = fs.String("root", "");
        var output = fs.String("output", "");
        var game = fs.String("game", "rimgovernor-trial");
        var headless = fs.Bool("headless", true);
        var timeout = fs.Duration("timeout", RunOptions.DefaultTimeout);
        var budget = fs.Duration("budget", TimeSpan.Zero);
        var stall = fs.Duration("stall", TimeSpan.Zero);
        var evidence = fs.String("evidence", "");
        var from = fs.String("from", "");
        var noDoctor = fs.Bool("no-doctor", false);
        var noHeal = fs.Bool("no-heal", false);
        var names = fs.ParseAfterPositionals(args);
        if (fs.Args.Count > 0)
        {
            throw new UsageException($"the case name must precede the flags: [{string.Join(' ', fs.Args)}]");
        }

        if (names.Count != 1)
        {
            throw new UsageException("dev takes exactly one case name (see `acceptance list`)");
        }

        if (root.Value.Length == 0)
        {
            throw new UsageException("-root is required");
        }

        if (!Path.IsPathFullyQualified(root.Value))
        {
            throw new UsageException($"-root must be absolute: {root.Value}");
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
        var outputDir = Path.GetFullPath(output.Value.Length == 0 ? Path.Combine(root.Value, "acceptance") : output.Value);
        var c = registry.Lookup(names[0])
            ?? throw new UsageException($"unknown case \"{names[0]}\" (see `acceptance list`)");
        var lint = c.Lint();
        if (lint.Count > 0)
        {
            throw new UsageException(string.Join("\n", lint));
        }

        return new DevOptions(
            c,
            new RunOptions
            {
                Root = root.Value,
                Output = outputDir,
                GameId = game.Value,
                Headless = headless.Value,
                Timeout = timeout.Value,
                Budget = budget.Value,
                Stall = stall.Value,
                Evidence = mode,
                From = from.Value,
                NoDoctor = noDoctor.Value,
                NoHeal = noHeal.Value,
                Dev = true,
            });
    }

    /// <summary>Runs the iterations; the trigger is Enter on <paramref name="stdin"/> (Go <c>devLoop</c>).</summary>
    public static async Task<int> LoopAsync(
        DevOptions d,
        RunDependencies dependencies,
        TextReader stdin,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        var opts = d.Options;
        if (!opts.NoDoctor && dependencies.Preflight is { } preflight)
        {
            var result = await preflight.RunAsync([d.Case], opts, stdout, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                return 2;
            }

            opts = opts with { Healed = result.Healed };
        }

        opts = opts with { Log = stdout };
        var runner = new CaseRunner(dependencies);

        // Iterations continue the numbering an earlier loop left under the output, since every run needs a fresh directory.
        var first = 1;
        while (Directory.Exists((opts with { Attempt = first }).CaseOutput(d.Case)))
        {
            first++;
        }

        var exit = 0;
        for (var n = first; ; n++)
        {
            opts = opts with { Attempt = n };
            var started = Stopwatch.StartNew();
            var outcome = await runner.ExecuteAsync(d.Case, opts, cancellationToken).ConfigureAwait(false);
            exit = outcome.Code;
            await PrintCaseAsync(stdout, d.Case, opts, outcome, started.Elapsed).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return exit;
            }

            await stdout.WriteAsync(string.Create(CultureInfo.InvariantCulture, $"dev: iteration {n} done; Enter reruns, q quits: ")).ConfigureAwait(false);
            string? line;
            try
            {
                line = await stdin.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                line = null;
            }

            if (line is null || line.Trim() == "q")
            {
                await stdout.WriteLineAsync().ConfigureAwait(false);
                return exit;
            }
        }
    }

    // Prints one iteration's verdict line, error, postmortem digest and drift flags (Go printCase).
    private static async Task PrintCaseAsync(TextWriter stdout, AcceptanceCase c, RunOptions opts, CaseOutcome outcome, TimeSpan wall)
    {
        var status = outcome.Code switch
        {
            Report.ExitBreak => "BREAK",
            0 => "PASS",
            _ => "FAIL",
        };
        var rounded = TimeSpan.FromMilliseconds(Math.Round(wall.TotalMilliseconds));
        await stdout.WriteLineAsync($"{status}\t{c.Name}\t{GoDuration.Format(rounded)}\t{Path.Combine(opts.CaseOutput(c), "result.json")}").ConfigureAwait(false);
        var error = outcome.Report["error"].AsString();
        if (error.Length > 0)
        {
            await stdout.WriteLineAsync($"\t{error}").ConfigureAwait(false);
        }

        outcome.Diagnosis?.Write(stdout);
        foreach (var flag in outcome.Drift)
        {
            await stdout.WriteLineAsync($"\tdrift: {flag}").ConfigureAwait(false);
        }
    }
}
