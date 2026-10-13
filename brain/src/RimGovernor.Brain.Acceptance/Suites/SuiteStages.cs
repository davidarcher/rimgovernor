using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One unit of suite work (Go <c>item</c>): a row run to its verdict (<see cref="Through"/> empty), or one declared stage
/// of it, opening on the stage <see cref="From"/> ("" for a fresh start) that item <see cref="Dep"/> produces (-1 when it
/// is cached or none).
/// </summary>
internal sealed record SuiteItem(SuiteEntry Entry, string Through, string From, int Dep)
{
    /// <summary>Names the item in logs and rows: the case, or case@stage.</summary>
    public string Label => Through.Length == 0 ? Entry.Name : Entry.Name + "@" + Through;
}

/// <summary>
/// Stage scheduling (Go <c>cmd/acceptance/stages.go</c>): under <c>suite -stages</c> a row whose case declares Stages is
/// not one work item but a chain of them, one per declared stage still missing from the bundles cached in <c>-root</c>
/// plus the tail that runs the case to its verdict. Each stage item is <c>acceptance run &lt;case&gt; -through
/// &lt;stage&gt;</c>: it opens on the newest cached bundle, captures the stage's own and ends. Its bundle is published
/// back into <c>-root</c> when it finishes, so the next item of the chain, on whichever worker is free, opens on it, and
/// the next suite (or <c>acceptance run</c>) in that root starts warm. A chain is serial by nature, so the fan-out is
/// across rows: once bundles are warm only tails run, one per case, in parallel. A row with nothing cached runs its whole
/// chain as one item: splitting a cold chain would only add a reload per stage.
/// <para>
/// The tail's row is the case's row and carries staged_from; the stage items' rows are listed under "stage_runs" and the
/// whole graph under "stages". An item whose producer failed is blocked, not run. The land tier refuses -stages.
/// </para>
/// </summary>
internal static class SuiteStages
{
    /// <summary>
    /// Plans the work items: one per row, or the chain of stage items a staged row still needs past <paramref name="cached"/>
    /// (its newest cached stage, "" when none) and its tail. Items keep list order, a chain's in stage order, so the
    /// queue's tiers and baseline ranking hold.
    /// </summary>
    internal static List<SuiteItem> Expand(IReadOnlyList<SuiteEntry> list, Func<SuiteEntry, string> cached)
    {
        var items = new List<SuiteItem>();
        foreach (var e in list)
        {
            var stages = e.Registered!.Stages;
            var have = cached(e);
            if (stages.Count == 0 || have.Length == 0)
            {
                items.Add(new SuiteItem(e, "", "", -1));
                continue;
            }

            var next = 0;
            for (var i = 0; i < stages.Count; i++)
            {
                if (stages[i] == have)
                {
                    next = i + 1;
                }
            }

            var from = have;
            var dep = -1;
            foreach (var name in stages.Skip(next))
            {
                items.Add(new SuiteItem(e, name, from, dep));
                from = name;
                dep = items.Count - 1;
            }

            items.Add(new SuiteItem(e, "", from, dep));
        }

        return items;
    }

    private enum ItemState
    {
        Pending,
        Running,
        Passed,
        Failed,
        Blocked,
    }

    /// <summary>
    /// Runs the items across the workers in list order, an item once the item it depends on has passed: <paramref name="run"/>
    /// (worker, index) reports whether the item passed, and every worker calls <paramref name="stop"/> once its queue is
    /// empty. Items whose producer failed (or was itself blocked) never run; the result names each with the index of the
    /// item that failed.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<int, int>> DispatchAsync(
        IReadOnlyList<SuiteItem> items,
        int workers,
        Func<int, int, Task<bool>> run,
        Func<int, Task> stop)
    {
        var state = new ItemState[items.Count];
        var blockedBy = new Dictionary<int, int>();
        var gate = new Lock();
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Picks the first runnable item, marking the ones whose producer failed as blocked on the way; -1 with wait true
        // means an item may still become runnable once a running one ends. Called under the gate.
        (int Index, bool Wait) Next()
        {
            var wait = false;
            for (var i = 0; i < items.Count; i++)
            {
                if (state[i] != ItemState.Pending)
                {
                    continue;
                }

                var dep = items[i].Dep;
                if (dep < 0)
                {
                    return (i, false);
                }

                switch (state[dep])
                {
                    case ItemState.Passed:
                        return (i, false);
                    case ItemState.Failed:
                        state[i] = ItemState.Blocked;
                        blockedBy[i] = dep;
                        break;
                    case ItemState.Blocked:
                        state[i] = ItemState.Blocked;
                        blockedBy[i] = blockedBy[dep];
                        break;
                    default:
                        wait = true;
                        break;
                }
            }

            return (-1, wait);
        }

        async Task WorkerAsync(int worker)
        {
            try
            {
                while (true)
                {
                    int index;
                    bool wait;
                    Task wake;
                    lock (gate)
                    {
                        (index, wait) = Next();
                        if (index >= 0)
                        {
                            state[index] = ItemState.Running;
                        }

                        wake = signal.Task;
                    }

                    if (index < 0)
                    {
                        if (!wait)
                        {
                            return;
                        }

                        await wake.ConfigureAwait(false);
                        continue;
                    }

                    var ok = await run(worker, index).ConfigureAwait(false);
                    TaskCompletionSource old;
                    lock (gate)
                    {
                        state[index] = ok ? ItemState.Passed : ItemState.Failed;
                        old = signal;
                        signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    old.SetResult();
                }
            }
            finally
            {
                await stop(worker).ConfigureAwait(false);
            }
        }

        var tasks = new List<Task>();
        for (var w = 1; w <= workers; w++)
        {
            var worker = w;
            tasks.Add(Task.Run(() => WorkerAsync(worker), CancellationToken.None));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return blockedBy;
    }

    /// <summary>Where a stage item's run writes: the suite output's stages/&lt;stage&gt; tree, beside the case's own row.</summary>
    internal static string StageOutput(SuiteOptions opts, SuiteItem it) =>
        it.Through.Length == 0 ? opts.Output : Path.Combine(opts.Output, "stages", it.Through);

    /// <summary>The case's stage bundle directory relative to a root.</summary>
    internal static string StagesRel(string name) => Path.Combine("stages", name.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Replaces the worker root's cached stage bundles of the case with the shared root's, so the item opens on what the suite has published so far.</summary>
    internal static void CarryStages(string root, string workerRoot, string name)
    {
        var src = Path.Combine(root, StagesRel(name));
        var dst = Path.Combine(workerRoot, StagesRel(name));
        if (Directory.Exists(dst))
        {
            Directory.Delete(dst, recursive: true);
        }

        if (Directory.Exists(src))
        {
            Checkpoints.CopyTree(src, dst);
        }
    }

    /// <summary>Copies every stage bundle the item left in the worker root over the shared root's, stage by stage, and returns the stages it published.</summary>
    internal static List<string> PublishStages(string root, string workerRoot, string name)
    {
        var src = Path.Combine(workerRoot, StagesRel(name));
        var dst = Path.Combine(root, StagesRel(name));
        var published = new List<string>();
        if (!Directory.Exists(src))
        {
            return published;
        }

        foreach (var dir in Directory.EnumerateDirectories(src).Order(StringComparer.Ordinal))
        {
            var stage = Path.GetFileName(dir);
            if (!File.Exists(Path.Combine(dir, Checkpoints.CheckpointSidecar)))
            {
                continue;
            }

            var target = Path.Combine(dst, stage);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Checkpoints.CopyTree(dir, target);
            published.Add(stage);
        }

        return published;
    }

    /// <summary>
    /// Runs one work item on a worker: under -stages a staged case's bundles are carried in from the shared root first and
    /// published back after, and a stage item writes under the suite output's stages tree. The row is the run's, plus
    /// "stage" and "from" for the graph.
    /// </summary>
    internal static async Task<JsonObject> RunItemAsync(
        SuiteItem it,
        SuiteOptions opts,
        string workerRoot,
        int worker,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var current = opts with { Output = StageOutput(opts, it) };
        var staged = opts.Stages && it.Entry.Registered!.Stages.Count > 0;
        if (staged)
        {
            try
            {
                CarryStages(opts.Root, workerRoot, it.Entry.Name);
            }
            catch (IOException e)
            {
                return CarryFailure(it, worker, e);
            }
            catch (UnauthorizedAccessException e)
            {
                return CarryFailure(it, worker, e);
            }
        }

        var entry = it.Entry with { Through = it.Through };
        var row = await SuiteRetry.RunEntryAsync(entry, current, workerRoot, worker, stderr, cancellationToken).ConfigureAwait(false);
        if (it.Through.Length > 0)
        {
            row["stage"] = it.Through;
        }

        if (it.From.Length > 0)
        {
            row["from"] = it.From;
        }

        if (staged)
        {
            try
            {
                var published = PublishStages(opts.Root, workerRoot, it.Entry.Name);
                if (published.Count > 0)
                {
                    row["published"] = new JsonArray([.. published.Select(p => (JsonNode?)JsonValue.Create(p))]);
                }
            }
            catch (IOException e)
            {
                await ReportPublishFailureAsync(row, it, worker, e, stderr).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException e)
            {
                await ReportPublishFailureAsync(row, it, worker, e, stderr).ConfigureAwait(false);
            }
        }

        return row;
    }

    private static JsonObject CarryFailure(SuiteItem it, int worker, Exception e) => new()
    {
        ["name"] = it.Entry.Name,
        ["stage"] = it.Through,
        ["worker"] = worker,
        ["passed"] = false,
        ["error"] = "carry stage bundles: " + e.Message,
    };

    private static async Task ReportPublishFailureAsync(JsonObject row, SuiteItem it, int worker, Exception e, TextWriter stderr)
    {
        row["publish_error"] = e.Message;
        await stderr.WriteLineAsync($"[worker {worker}] {it.Label}: publish stage bundles: {e.Message}").ConfigureAwait(false);
    }

    /// <summary>The row of an item that never ran because the item at <paramref name="cause"/> failed.</summary>
    internal static JsonObject BlockedRow(SuiteItem it, SuiteItem cause)
    {
        var row = new JsonObject
        {
            ["name"] = it.Entry.Name,
            ["passed"] = false,
            ["blocked_by"] = cause.Label,
            ["error"] = "blocked: " + cause.Label + " failed, so its bundle is missing",
        };
        if (it.Through.Length > 0)
        {
            row["stage"] = it.Through;
        }

        if (it.From.Length > 0)
        {
            row["from"] = it.From;
        }

        return row;
    }

    /// <summary>The report's "stages" block: per staged row, the stage it was cached through and each item's outcome in chain order.</summary>
    internal static JsonArray StageGraph(IReadOnlyList<SuiteItem> items, IReadOnlyList<JsonObject> rows)
    {
        var graph = new JsonArray();
        var byCase = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Entry.Registered!.Stages.Count == 0)
            {
                continue;
            }

            if (!byCase.TryGetValue(it.Entry.Name, out var g))
            {
                g = new JsonObject
                {
                    ["case"] = it.Entry.Name,
                    ["declared"] = new JsonArray([.. it.Entry.Registered.Stages.Select(s => (JsonNode?)JsonValue.Create(s))]),
                    ["cached_through"] = it.From,
                    ["items"] = new JsonArray(),
                };
                byCase[it.Entry.Name] = g;
                graph.Add(g);
            }

            var row = rows[i];
            var node = new JsonObject
            {
                ["stage"] = it.Through.Length == 0 ? "(tail)" : it.Through,
                ["from"] = it.From,
                ["passed"] = row["passed"]?.DeepClone(),
            };
            foreach (var key in new[] { "worker", "wall_ms", "output", "blocked_by", "published", "error" })
            {
                if (row[key] is { } v)
                {
                    node[key] = v.DeepClone();
                }
            }

            ((JsonArray)g["items"]!).Add(node);
        }

        return graph;
    }
}
