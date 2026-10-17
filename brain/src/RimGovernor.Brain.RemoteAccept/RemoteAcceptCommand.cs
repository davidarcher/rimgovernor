using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Acceptance;

namespace RimGovernor.Brain.RemoteAccept;

/// <summary>
/// The <c>remoteaccept</c> command: <c>RimGovernor.cmd remoteaccept &lt;subcommand&gt;</c>.
/// Aggregates shard evidence or imports an authenticated GitHub Actions artifact for <c>land</c>; it never publishes
/// source or dispatches.
/// </summary>
public static class RemoteAcceptCommand
{
    private const string Usage = """
        usage: remoteaccept aggregate|import|export|soak|index|casetimes [flags]
               remoteaccept progress -prepare|-watch|-finish|-sweep [flags]   (display only; credential on stdin)

          export     -root DIR -shard ID -jobs FILE     copy a shard's public diagnostics and write <shard>/attempts.json
          aggregate  -root DIR [-shards FILE]           recompute the verdict; write aggregate.json and result.json
          index      -root DIR                          write index.json and print the case index (GITHUB_STEP_SUMMARY when set)
          soak       -root DIR -soak DIR -repeat N      per-case pass rates over a repeat=N soak
          import     -output DIR -artifact ID -run ID [-attempt N] [-repo DIR]   authenticate and verify an Actions artifact
          casetimes  -root DIR [-output FILE]           regenerate the measured case-times table
          progress   -prepare|-watch|-finish|-sweep     live check-run progress
        """;

    /// <summary>Runs one subcommand and returns the process exit code (2 usage, 1 failure); progress is display only and always returns 0.</summary>
    public static Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken) =>
        RunAsync(args, registry: null, Stream.Null, stdout, stderr, cancellationToken);

    /// <summary>
    /// Runs one subcommand. <paramref name="registry"/> is the trusted case registry <c>progress -prepare</c> maps selection
    /// names to output paths with; <paramref name="stdin"/> carries the progress credential.
    /// </summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, CaseRegistry? registry, Stream stdin, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (args.Count > 0 && args[0] == "progress")
        {
            // Display-only: API, startup and observation failures never change verdicts.
            try
            {
                await ProgressCommand.RunAsync([.. args.Skip(1)], registry, stdin, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await stderr.WriteLineAsync("remoteaccept: live progress unavailable; native verdict is unaffected").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await stderr.WriteLineAsync("remoteaccept: live progress unavailable; native verdict is unaffected").ConfigureAwait(false);
            }

            return 0;
        }

        if (args.Count == 0)
        {
            await stderr.WriteLineAsync("remoteaccept: usage: remoteaccept aggregate|import|export|soak|index|casetimes [flags]").ConfigureAwait(false);
            return 2;
        }

        if (args[0] is "-h" or "-help" or "--help" or "help")
        {
            await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
            return 0;
        }

        try
        {
            await RunSubcommandAsync(args, stdout, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (RemoteAcceptUsageException e)
        {
            await stderr.WriteLineAsync("remoteaccept: " + e.Message).ConfigureAwait(false);
            return 2;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await stderr.WriteLineAsync("remoteaccept: " + e.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task RunSubcommandAsync(IReadOnlyList<string> args, TextWriter stdout, CancellationToken cancellationToken)
    {
        var f = new Flags();
        var root = f.String("root", "");
        var shards = f.String("shards", "shards.json");
        var output = f.String("output", "");
        var repo = f.String("repo", ".");
        var id = f.Int64("artifact", 0);
        var runId = f.Int64("run", 0);
        var attempt = f.Int("attempt", 1);
        var shard = f.String("shard", "");
        var jobsFile = f.String("jobs", "");
        var soak = f.String("soak", "");
        var repeat = f.Int("repeat", 1);
        f.Parse([.. args.Skip(1)]);
        if (f.Args.Count != 0)
        {
            throw new RemoteAcceptUsageException("unexpected arguments");
        }

        switch (args[0])
        {
            case "casetimes":
                {
                    var times = CaseTimes.Collect(root.Value);
                    await File.WriteAllTextAsync(output.Value.Length > 0 ? output.Value : DefaultCaseTimesPath, CaseTimes.Encode(times), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                    break;
                }

            case "export":
                {
                    var jobs = ExportJob.ParseJobs(await File.ReadAllBytesAsync(jobsFile.Value, cancellationToken).ConfigureAwait(false));
                    RemoteExport.ExportShard(root.Value, shard.Value, jobs, [Environment.GetEnvironmentVariable("GH_TOKEN") ?? "", Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? ""]);
                    break;
                }

            case "aggregate":
                await AggregateAsync(root.Value, shards.Value, stdout).ConfigureAwait(false);
                break;
            case "index":
                await IndexAsync(root.Value, stdout, cancellationToken).ConfigureAwait(false);
                break;
            case "soak":
                foreach (var rate in RemoteSoak.Rates(root.Value, soak.Value, repeat.Value))
                {
                    await stdout.WriteLineAsync(rate.ToString()).ConfigureAwait(false);
                }

                break;
            case "import":
                {
                    if (output.Value.Length == 0)
                    {
                        throw new RemoteAcceptUsageException("-output is required");
                    }

                    var trust = await RemoteImport.LocalTrustAsync(repo.Value, cancellationToken).ConfigureAwait(false);
                    var provenance = new Provenance(trust, id.Value, "", runId.Value, attempt.Value);
                    await RemoteImport.DownloadAsync(provenance, output.Value, cancellationToken).ConfigureAwait(false);
                    await stdout.WriteLineAsync($"verified import: {Path.Combine(output.Value, "evidence", "result.json")}").ConfigureAwait(false);
                    break;
                }

            default:
                throw new RemoteAcceptUsageException($"unknown command \"{args[0]}\"");
        }
    }

    /// <summary>Where the case-times table lives; the planner embeds this file.</summary>
    private static string DefaultCaseTimesPath => Path.Combine("brain", "src", "RimGovernor.Brain.Acceptance", "Suites", "Data", "casetimes.json");

    private static async Task AggregateAsync(string root, string shardsFile, TextWriter stdout)
    {
        if (root.Length == 0)
        {
            throw new RemoteAcceptUsageException("-root is required");
        }

        var runRef = RemoteEvidence.FileRef(root, "run.json");
        var selectionRef = RemoteEvidence.FileRef(root, "selection.json");
        var shards = StrictJson.Elements(StrictJson.ParseArray(await File.ReadAllBytesAsync(Path.Combine(root, shardsFile)).ConfigureAwait(false), shardsFile), Shard.Parse);
        Evaluation e;
        string? evalError = null;
        try
        {
            e = RemoteEvidence.Evaluate(root, runRef, selectionRef, shards);
        }
        catch (EvaluationFailedException failed)
        {
            e = failed.Partial;
            evalError = failed.Message;
            e.Aggregate = e.Aggregate with { Passed = false, Status = "incomplete", Error = evalError };
        }

        var aggregateRef = RemoteEvidence.WriteAggregate(root, "aggregate.json", e.Aggregate);
        if (evalError is not null)
        {
            e.Report = e.Report with { Passed = false, Error = e.Aggregate.Error, Remote = e.Report.Remote with { Aggregate = aggregateRef } };
            RemoteEvidence.WriteReport(root, "result.json", e.Report);
            throw new RemoteAcceptException(evalError);
        }

        e.Report = e.Report with { Remote = e.Report.Remote with { Aggregate = aggregateRef } };
        RemoteEvidence.WriteReport(root, "result.json", e.Report);
        if (!e.Aggregate.Passed)
        {
            throw new RemoteAcceptException($"{e.Aggregate.Status}: {e.Aggregate.Error}");
        }

        await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{e.Aggregate.Cases.Count} cases passed; aggregate.json and result.json written (import required before landing)")).ConfigureAwait(false);
    }

    private static async Task IndexAsync(string root, TextWriter stdout, CancellationToken cancellationToken)
    {
        // Display only: the workflow step is continue-on-error.
        if (root.Length == 0)
        {
            throw new RemoteAcceptUsageException("-root is required");
        }

        var index = EvidenceIndex.Build(root);
        index.WriteTo(root);
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
        {
            await File.AppendAllTextAsync(summary, index.Markdown(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return;
        }

        await stdout.WriteAsync(index.Markdown()).ConfigureAwait(false);
    }
}

/// <summary><c>remoteaccept progress</c>.</summary>
internal static class ProgressCommand
{
    public static async Task RunAsync(IReadOnlyList<string> args, CaseRegistry? registry, Stream stdin, CancellationToken cancellationToken)
    {
        var f = new Flags();
        var planFile = f.String("plan", @"C:\rg\progress.json");
        var idFile = f.String("id-file", @"C:\rg\progress-id.txt");
        var stopFile = f.String("stop-file", @"C:\rg\progress.stop");
        var root = f.String("root", @"C:\rg\evidence");
        var outputRoot = f.String("output-root", @"C:\rg");
        var shard = f.String("shard", Environment.GetEnvironmentVariable("SHARD") ?? "");
        var prepare = f.Bool("prepare", false);
        var watch = f.Bool("watch", false);
        var finish = f.Bool("finish", false);
        var sweep = f.Bool("sweep", false);
        var artifact = f.String("artifact-url", "");
        f.Parse(args);
        var modes = new[] { prepare.Value, watch.Value, finish.Value, sweep.Value }.Count(static m => m);
        if (modes != 1 || f.Args.Count != 0)
        {
            throw new RemoteAcceptUsageException("choose one progress mode");
        }

        if (prepare.Value)
        {
            var p = Prepare(registry, root.Value, shard.Value, outputRoot.Value);
            await File.WriteAllBytesAsync(planFile.Value, p.ToJson(), cancellationToken).ConfigureAwait(false);
            return;
        }

        // Credentials are accepted only through stdin, including finish and sweep.
        var buffer = new byte[16385];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await stdin.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false)) > 0)
        {
            read += n;
        }

        var credential = Encoding.UTF8.GetString(buffer, 0, read);
        if (read > 16384 || string.IsNullOrWhiteSpace(credential))
        {
            throw new RemoteAcceptException("missing progress credential");
        }

        var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "";
        if (!EvidenceRules.IsRepository(repo))
        {
            throw new RemoteAcceptException("invalid repository");
        }

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var api = new ProgressApi(client, "https://api.github.com", repo, credential.Trim());
        var headSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "";
        var runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "";
        var attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "";
        if (sweep.Value)
        {
            if (!EvidenceRules.IsOid(headSha) || !EvidenceRules.IsPositiveDecimal(runId) || !EvidenceRules.IsPositiveDecimal(attempt))
            {
                throw new RemoteAcceptException("invalid sweep identity");
            }

            await api.SweepAsync(headSha, runId + ":" + attempt + ":", cancellationToken).ConfigureAwait(false);
            return;
        }

        var plan = ProgressPlan.Parse(ProgressApi.ReadProgressJson(planFile.Value));
        Validate(plan, registry);
        if (plan.HeadSha != headSha || plan.RunId != runId || plan.Attempt != attempt)
        {
            throw new RemoteAcceptException("wrong progress invocation");
        }

        if (finish.Value)
        {
            await api.FinishAsync(idFile.Value, plan.ExternalId, artifact.Value, cancellationToken).ConfigureAwait(false);
            return;
        }

        await api.WatchAsync(plan, idFile.Value, stopFile.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs from the trusted checkout before tested binaries start. The registry, not directory enumeration or tested output, owns the mapping.</summary>
    private static ProgressPlan Prepare(CaseRegistry? registry, string root, string shard, string outputRoot)
    {
        var selection = ProgressApi.ReadProgressJson(Path.Combine(root, "selection.json"));
        var run = ProgressApi.ReadProgressJson(Path.Combine(root, "run.json"));
        var testedCommit = StrictJson.LooseStr(run, "tested_commit");
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in StrictJson.LooseArr(selection, "shards"))
        {
            var o = StrictJson.ElementObj(s);
            if (StrictJson.LooseStr(o, "id") == shard)
            {
                foreach (var n in StrictJson.LooseArr(o, "cases"))
                {
                    selected.Add(StrictJson.ElementStr(n));
                }
            }
        }

        var names = new List<string>();
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in StrictJson.LooseArr(selection, "cases"))
        {
            var row = StrictJson.ElementObj(item);
            var name = StrictJson.LooseStr(row, "name");
            if (!selected.Contains(name) || StrictJson.LooseStr(row, "mod_role") != "fixture")
            {
                continue;
            }

            var registered = registry?.Lookup(name) ?? throw new RemoteAcceptException("case unavailable in trusted registry");
            if (outputs.ContainsKey(name))
            {
                throw new RemoteAcceptException("duplicate progress case");
            }

            names.Add(name);
            var dir = new RunOptions { Output = Path.Combine(outputRoot, "fixture", "job", "out") }.CaseOutput(registered);
            outputs[name] = Path.Combine(dir, "result.json");
        }

        var p = new ProgressPlan(
            shard,
            Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "",
            Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "",
            Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "",
            testedCommit,
            names,
            outputs);
        if (names.Count != selected.Count)
        {
            throw new RemoteAcceptException("incomplete progress projection");
        }

        Validate(p, registry);
        return p;
    }

    private static void Validate(ProgressPlan p, CaseRegistry? registry)
    {
        if (!EvidenceRules.IsShardId(p.Shard) || !EvidenceRules.IsPositiveDecimal(p.RunId) || !EvidenceRules.IsPositiveDecimal(p.Attempt)
            || !EvidenceRules.IsOid(p.HeadSha) || !EvidenceRules.IsOid(p.TestedCommit) || p.Cases.Count == 0)
        {
            throw new RemoteAcceptException("invalid progress identity");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in p.Cases)
        {
            if (registry?.Lookup(name) is null || !seen.Add(name) || !p.Outputs.TryGetValue(name, out var output) || !Path.IsPathFullyQualified(output))
            {
                throw new RemoteAcceptException("invalid progress case");
            }
        }
    }
}
