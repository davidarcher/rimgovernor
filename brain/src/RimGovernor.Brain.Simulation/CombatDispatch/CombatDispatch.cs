using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace RimGovernor.Brain.Simulation.CombatDispatch;

// Port of Go internal/combatdispatch: runs a combat lab sweep's seeds on at most two workers and reduces
// the results in seed order (#2839).
//
// Seeds are independent, so two workers (each with its own game root, or its own remote job) may run them
// concurrently. Two is the ceiling: four concurrent remote jobs cancelled three at the 15 minute mark, and
// a loaded box cuts runs and distorts native timing. The reduction sorts by seed, never by completion
// order, so scheduling cannot change the output. The report records the concurrency as evidence; any
// metric that depends on wall time (WallMs, per-run step latency) is excluded from comparisons between
// reports.

/// <summary>One seed's outcome.</summary>
public sealed record DispatchRun<T>
{
    [JsonPropertyName("seed")]
    public int Seed { get; init; }

    [JsonPropertyName("worker")]
    public int Worker { get; init; }

    [JsonPropertyName("value")]
    public T? Value { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

/// <summary>The seed-ordered reduction plus the concurrency it ran under.</summary>
public sealed record DispatchReport<T>
{
    [JsonPropertyName("workers")]
    public int Workers { get; init; }

    /// <summary>Notes the box load at dispatch (see <see cref="CombatDispatcher.LoadNote"/>).</summary>
    [JsonPropertyName("load")]
    public string Load { get; init; } = "";

    /// <summary>The whole dispatch's wall time; do not compare it across reports taken at different concurrency or load.</summary>
    [JsonPropertyName("wall_ms")]
    public long WallMs { get; init; }

    [JsonPropertyName("runs")]
    public IReadOnlyList<DispatchRun<T>> Runs { get; init; } = [];
}

/// <summary>The dispatch failed; the full report is beside it (Go returns the report with the error).</summary>
public sealed class DispatchException<T>(string message, DispatchReport<T> report) : Exception(message)
{
    public DispatchReport<T> Report { get; } = report;
}

public static class CombatDispatcher
{
    /// <summary>The most runs that may be in flight at once.</summary>
    public const int MaxWorkers = 2;

    /// <summary>
    /// Runs <paramref name="fn"/> once per seed on the given number of workers and returns the runs sorted by
    /// seed. <paramref name="fn"/> receives the worker index (0..workers-1), which maps to that worker's game
    /// root; one worker never runs two seeds at once. A failing run is recorded, does not stop the others,
    /// and is thrown (the first failure by seed) as a <see cref="DispatchException{T}"/> carrying the full
    /// report. Workers above <see cref="MaxWorkers"/>, below 1, or duplicate seeds are refused before anything
    /// runs.
    /// </summary>
    public static async Task<DispatchReport<T>> Dispatch<T>(
        IReadOnlyList<int> seeds,
        int workers,
        string load,
        Func<int, int, CancellationToken, Task<T>> fn,
        CancellationToken cancellationToken)
    {
        var report = new DispatchReport<T> { Workers = workers, Load = load };
        if (workers < 1 || workers > MaxWorkers)
        {
            throw new DispatchException<T>($"combat lab dispatch: {workers} workers, want 1 to {MaxWorkers}", report);
        }
        var ordered = seeds.Order().ToArray();
        if (ordered.Distinct().Count() != ordered.Length)
        {
            throw new DispatchException<T>($"combat lab dispatch: duplicate seeds in [{string.Join(' ', seeds)}]", report);
        }
        var clock = Stopwatch.StartNew();
        var runs = new DispatchRun<T>[ordered.Length];
        var next = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleWriter = true });
        var tasks = new List<Task>();
        for (var w = 0; w < workers; w++)
        {
            var worker = w;
            tasks.Add(Task.Run(
                async () =>
                {
                    await foreach (var i in next.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        var run = new DispatchRun<T> { Seed = ordered[i], Worker = worker };
                        try
                        {
                            run = run with { Value = await fn(worker, ordered[i], cancellationToken).ConfigureAwait(false) };
                        }
                        catch (Exception ex)
                        {
                            run = run with { Error = ex.Message };
                        }
                        runs[i] = run; // each index has one writer
                    }
                },
                CancellationToken.None));
        }
        for (var i = 0; i < ordered.Length; i++)
        {
            next.Writer.TryWrite(i);
        }
        next.Writer.Complete();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        report = report with { WallMs = clock.ElapsedMilliseconds, Runs = runs };
        foreach (var run in runs)
        {
            if (!string.IsNullOrEmpty(run.Error))
            {
                throw new DispatchException<T>($"combat lab dispatch: seed {run.Seed}: {run.Error}", report);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return report;
    }

    /// <summary>
    /// Describes the box load for the report: the RimWorld processes running now. Call it before dispatching,
    /// so the workers are not counted. Native timing degrades with peers, so the count is the evidence a
    /// reader weighs a timing-dependent metric against.
    /// </summary>
    public static string LoadNote()
    {
        try
        {
            var count = Process.GetProcessesByName("RimWorld").Length;
            return string.Create(CultureInfo.InvariantCulture, $"rimworld processes before dispatch: {count}");
        }
        catch (InvalidOperationException)
        {
            return "rimworld processes: unknown";
        }
    }
}
