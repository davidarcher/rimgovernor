using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The suite engine.
/// <para>
/// Scheduling: one shared queue in three tiers. Bridge-only cases that keep the process come first; cases that end or
/// replace it (NoKeep: a shutdown, a fault, an owned lifecycle; Rendered: a windowed profile the headless worker cannot
/// serve) follow, so the kept process is reused as long as possible; serve-driven cases (Serve or Service) are last, so a
/// worker that has hosted a service never runs a bridge-only case on that process afterwards. Within each tier the queue
/// is longest-first by the <c>-baseline</c> suite's wall times (untimed cases first, as if long), so a slow case does not
/// land last and idle the other workers.
/// </para>
/// <para>
/// <c>-baseline</c> also drives regression flagging: a case whose run time (wall time net of the game boot, so queue
/// placement does not count) is more than <see cref="RegressionRatio"/> times its baseline's and
/// <see cref="RegressionFloorMs"/> over it is listed under "regressions" (the list never fails the suite on its own) and
/// the report carries the sum of case wall times beside the baseline's.
/// </para>
/// <para>
/// Every row's metrics block (result.json "metrics") is appended by the run to the series at <c>-series</c> (default
/// <c>&lt;output&gt;/../metrics.jsonl</c>, shared by successive suites beside each other), and the metrics past their rule
/// against the series' trailing median are listed on the row under "drift" and, over all rows, under the suite's "drift".
/// Every row also carries the run's world block (seed, save and fixture hashes) and its flake record (the share of the
/// case's recent series runs that failed). A suite used as <c>-baseline</c> hands that record on: a regression row shows
/// its baseline's flake beside the ratio, and a failed row prints with its own, so a known flake reads as one.
/// </para>
/// </summary>
public static class SuiteRunner
{
    /// <summary>
    /// The run-time growth over the baseline that flags a case as a regression: on a ~10s case the peer load of the other
    /// workers moves the time by more than 25% run to run.
    /// </summary>
    public const double RegressionRatio = 1.25;

    /// <summary>The absolute growth, in milliseconds, a regression must also exceed.</summary>
    public const long RegressionFloorMs = 5000;

    /// <summary>
    /// Orders the queue by tier (kept-process rows, process-ending rows, serve-driven rows), each tier longest-first by the
    /// baseline. The sort is stable, so equal or untimed rows keep their listed order.
    /// </summary>
    public static IReadOnlyList<SuiteEntry> Schedule(IReadOnlyList<SuiteEntry> list, SuiteBaseline? b)
    {
        ArgumentNullException.ThrowIfNull(list);
        return [.. list
            .OrderBy(e => e.Tier)
            .ThenByDescending(e =>
            {
                var rank = SuiteBaseline.Rank(b, e);
                return rank < 0 ? double.PositiveInfinity : rank;
            })];
    }

    /// <summary>Lists the rows over their baseline, in queue order, and the baseline's total wall time over the rows it timed.</summary>
    internal static (IReadOnlyList<SuiteRegression> Regressions, long BaselineTotal) Regressions(IReadOnlyList<SuiteRow> rows, SuiteBaseline b)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(b);
        var list = new List<SuiteRegression>();
        long baselineTotal = 0;
        foreach (var row in rows)
        {
            var name = row.Name ?? "";
            if (!b.TryWall(name, out var baseWall) || baseWall <= 0)
            {
                continue;
            }

            baselineTotal += (long)baseWall;
            var baseRun = baseWall - b.BootOf(name);
            var wall = row.WallMs ?? 0;
            var boot = row.BootMs ?? 0;
            double run = wall - boot;
            if (baseRun > 0 && run > baseRun * RegressionRatio && run - baseRun > RegressionFloorMs)
            {
                list.Add(new SuiteRegression(name, wall, (long)baseWall, (long)run, (long)baseRun, run / baseRun, b.FlakeOf(name)));
            }
        }

        return (list, baselineTotal);
    }

    /// <summary>Collects the rows' drift flags (each row's result.json "drift", as the run computed them) in queue order.</summary>
    internal static IReadOnlyList<DriftFlag> DriftFlags(IReadOnlyList<SuiteRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. rows.SelectMany(row => row.Drift ?? [])];
    }

    /// <summary>
    /// Executes the rows across the workers and writes the suite's result.json under <c>opts.Output</c>; returns the
    /// process exit code.
    /// </summary>
    public static async Task<int> RunSuiteAsync(
        IReadOnlyList<SuiteEntry> rowsIn,
        SuiteOptions opts,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rowsIn);
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(stderr);
        try
        {
            Directory.CreateDirectory(opts.Output);
        }
        catch (IOException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }

        if (Directory.EnumerateFileSystemEntries(opts.Output).Any())
        {
            await stderr.WriteLineAsync("-output must be a fresh, empty directory").ConfigureAwait(false);
            return 2;
        }

        var workers = Math.Min(opts.Workers, rowsIn.Count);
        var report = Report.Create(
            string.Create(CultureInfo.InvariantCulture, $"{rowsIn.Count} acceptance cases across {workers} private game copies, one kept process per worker"),
            headless: true);
        report["workers"] = workers;
        report["retry_policy"] = SuiteRetry.RetryPolicy;
        if (opts.Tier.Length > 0)
        {
            report["tier"] = opts.Tier;
        }

        SuiteBaseline? baseline = null;
        if (opts.Baseline.Length > 0)
        {
            try
            {
                baseline = SuiteBaseline.Load(opts.Baseline);
            }
            catch (AcceptanceException e)
            {
                report["error"] = e.Message;
                return report.Finalize(opts.Output);
            }

            report["baseline"] = opts.Baseline;
        }

        var list = Schedule(rowsIn, baseline);

        // The landing gate never rebuilds the mod on its own (NoHeal); the rows' fixture ops still let the preflight name
        // a fixture the build lacks. Cases go unnamed so no row's output directory counts as occupied here (each worker
        // checks its own).
        var ops = list.Where(e => e.Registered is not null).SelectMany(e => e.Registered!.FixtureOps()).Distinct(StringComparer.Ordinal).ToList();
        var pre = await PreflightPass.RunAsync(
            [],
            ops,
            new RunOptions { Root = opts.Root, Output = opts.Output, GameId = opts.GameId, NoHeal = true },
            stderr,
            cancellationToken).ConfigureAwait(false);
        if (!pre.Ok)
        {
            report["error"] = "doctor preflight failed on " + opts.Root;
            return report.Finalize(opts.Output);
        }

        using var suiteCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        suiteCts.CancelAfter(opts.Timeout);
        var ctx = suiteCts.Token;

        // Worker roots: clones of -root, each launching its own process of the shared installation.
        var roots = new string[workers];
        for (var i = 0; i < roots.Length; i++)
        {
            var dest = Path.Combine(opts.Output, "workers", (i + 1).ToString(CultureInfo.InvariantCulture));
            try
            {
                _ = HeadlessProfile.IsolatedRoot(opts.Root, dest);
            }
            catch (AcceptanceException e)
            {
                report["error"] = string.Create(CultureInfo.InvariantCulture, $"worker {i + 1} root: {e.Message}");
                return report.Finalize(opts.Output);
            }

            roots[i] = dest;
        }

        report["worker_roots"] = OutputJson.Array(roots);

        var rows = new SuiteRow?[list.Count];
        var gate = new Lock();
        var next = -1;
        var clock = Stopwatch.StartNew();

        // One shared queue: each worker takes the next row in schedule order until none is left, then stops its game.
        async Task WorkerAsync(int worker)
        {
            try
            {
                for (var i = Interlocked.Increment(ref next); i < list.Count; i = Interlocked.Increment(ref next))
                {
                    SuiteRow row;
                    try
                    {
                        row = await SuiteRetry.RunEntryAsync(list[i], opts, roots[worker - 1], worker, stderr, ctx).ConfigureAwait(false);
                    }
                    catch (AcceptanceException e)
                    {
                        row = new SuiteRow { Name = list[i].Name, Worker = worker, Error = e.Message };
                    }

                    rows[i] = row;
                }
            }
            finally
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                string? stopError = null;
                try
                {
                    _ = await Game.StopGameAsync(roots[worker - 1], opts.GameId, stopCts.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is AcceptanceException or OperationCanceledException)
                {
                    stopError = e.Message;
                }

                if (stopError is not null)
                {
                    lock (gate)
                    {
                        report[string.Create(CultureInfo.InvariantCulture, $"worker_{worker}_stop_error")] = stopError;
                    }
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(1, workers).Select(w => Task.Run(() => WorkerAsync(w), CancellationToken.None))).ConfigureAwait(false);
        report["wall_ms"] = clock.ElapsedMilliseconds;
        var caseRows = rows.Select(r => r ?? new SuiteRow()).ToList();
        var passed = 0;
        var failed = new List<string>();
        long total = 0;
        foreach (var row in caseRows)
        {
            total += row.WallMs ?? 0;
            if (row.Passed)
            {
                passed++;
            }
            else
            {
                failed.Add(row.Name ?? "");
                if (row.Flake is { Failures: > 0 } flake)
                {
                    await stderr.WriteLineAsync($"{row.Name} failed and is a known flake: {flake} before this run").ConfigureAwait(false);
                }
            }
        }

        report["cases_passed"] = passed;
        report["total_case_ms"] = total;
        if (baseline is not null)
        {
            var (regressions, baselineTotal) = Regressions(caseRows, baseline);
            report["regressions"] = OutputJson.Array(regressions, r => r.ToJson());
            report["baseline_total_ms"] = baselineTotal;
            if (regressions.Count > 0)
            {
                var text = string.Join(", ", regressions.Select(r => r.ToString()));
                await stderr.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"regressions (>{(RegressionRatio - 1) * 100:0}% and >{RegressionFloorMs / 1000}s over baseline, net of boot): {text}")).ConfigureAwait(false);
            }
        }

        if (!opts.NoSeries)
        {
            report["series"] = opts.Series;
            var flags = DriftFlags(caseRows);
            report["drift"] = OutputJson.Array(flags, f => f.ToJson());
            if (flags.Count > 0)
            {
                await stderr.WriteLineAsync($"drift (past the metric's rule against the trailing median of {opts.Series}):").ConfigureAwait(false);
                foreach (var f in flags)
                {
                    await stderr.WriteLineAsync($"  {f}").ConfigureAwait(false);
                }
            }
        }

        report["cases"] = OutputJson.Array(caseRows, row => row.ToJson());
        if (failed.Count > 0)
        {
            report["failed"] = OutputJson.Array(failed);
            report["error"] = string.Create(CultureInfo.InvariantCulture, $"{failed.Count} of {caseRows.Count} cases failed: {string.Join(", ", failed)}");
        }
        else
        {
            report["passed"] = true;
        }

        return report.Finalize(opts.Output);
    }

    /// <summary>Where a case's evidence and result.json go under a run's output directory.</summary>
    internal static string CaseOutput(string output, string caseName) =>
        Path.Combine(output, caseName.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The argv (after the executable) a row runs as on a worker, <c>acceptance run</c> on this executable, and where the
    /// row's result.json lands. The suite ran the preflight once on the shared root; a worker's process check would only
    /// see its siblings.
    /// </summary>
    internal static (List<string> Argv, string Output) EntryCommand(SuiteEntry e, SuiteOptions opts, string workerRoot)
    {
        var tail = new List<string> { "run", e.Name, "-root", workerRoot, "-output", opts.Output, "-game", opts.GameId, "-no-doctor" };
        if (opts.NoSeries)
        {
            tail.Add("-no-series");
        }
        else if (opts.Series.Length > 0)
        {
            tail.AddRange(["-series", opts.Series]);
        }

        foreach (var (name, d) in new[] { ("-timeout", opts.CaseTimeout), ("-budget", opts.Budget), ("-stall", opts.Stall) })
        {
            if (d > TimeSpan.Zero)
            {
                tail.AddRange([name, GoDuration.Format(d)]);
            }
        }

        if (opts.Evidence.Length > 0)
        {
            tail.AddRange(["-evidence", opts.Evidence]);
        }

        return ([.. SelfInvocation.AcceptanceArgv(tail)], CaseOutput(opts.Output, e.Name));
    }

    /// <summary>
    /// Executes one row on a worker's root with the game kept between rows, and returns its report row: exit code, wall
    /// and boot time, output directory and the row's own result.json verdict.
    /// </summary>
    internal static async Task<SuiteRow> RunEntryOnceAsync(
        SuiteEntry e,
        SuiteOptions opts,
        string workerRoot,
        int worker,
        TextWriter stderr,
        CancellationToken ctx)
    {
        var (argv, output) = EntryCommand(e, opts, workerRoot);
        var (self, _) = SelfInvocation.Resolve();
        var row = new SuiteRow
        {
            Name = e.Name,
            Worker = worker,
            Output = output,
            Argv = [self, .. argv],
            Serve = e.ServeDriven,
            Acceptance = e.Acceptance.Length > 0 ? e.Acceptance : null,
        };

        var logPath = output + ".log";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        }
        catch (IOException ex)
        {
            row.Error = ex.Message;
            return row;
        }

        var label = e.Name;
        await stderr.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"[worker {worker}] {label}")).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();

        // Explicit even though it is the default: a caller's opt-out must not leak into the workers, which stop their game
        // once at the end.
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { [Game.KeepGameEnv] = "1" };
        var exit = -1;
        var failedToStart = false;
        try
        {
            var result = await ChildProcess.RunAsync(self, argv, env, logPath, ctx).ConfigureAwait(false);
            exit = result.Code;
            row.RecordExit(exit);
        }
        catch (OperationCanceledException)
        {
            // The kill ended the child, as exec.CommandContext's did: no clean exit status.
            row.RecordExit(exit);
        }
        catch (AcceptanceException ex)
        {
            failedToStart = true;
            row.RecordExit(null); // no child process exit status (failed to start)
            row.Error = ex.Message;
        }

        row.WallMs = clock.ElapsedMilliseconds;
        row.Log = logPath;
        var runFailed = failedToStart || exit != 0;
        var resultPath = Path.Combine(output, "result.json");
        CaseResult? parsed = null;
        string? readError = null;
        try
        {
            parsed = JsonSerializer.Deserialize(File.ReadAllText(resultPath), SuiteJsonContext.Default.CaseResult);
        }
        catch (IOException ex)
        {
            readError = ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            readError = ex.Message;
        }
        catch (JsonException ex)
        {
            readError = "decode " + resultPath + ": " + ex.Message;
        }

        if (parsed is not null)
        {
            row.ApplyResult(parsed, runFailed);
        }
        else if (readError is not null && row.Error is null)
        {
            row.Error = "no result.json: " + readError;
        }

        var exitText = row.Exit is { } code ? code.ToString(CultureInfo.InvariantCulture) : "<nil>";
        var passedText = row.Passed ? "true" : "false";
        await stderr.WriteLineAsync($"[worker {worker}] {label} exit={exitText} passed={passedText} {GoDuration.Format(TimeSpan.FromSeconds(Math.Round(clock.Elapsed.TotalSeconds)))}").ConfigureAwait(false);
        if (!row.Passed && row.Error is not null)
        {
            await stderr.WriteLineAsync($"[worker {worker}] {label} error: {row.Error}").ConfigureAwait(false);
        }

        return row;
    }
}
