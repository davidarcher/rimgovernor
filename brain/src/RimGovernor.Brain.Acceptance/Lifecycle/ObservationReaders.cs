using System.Diagnostics;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// What the observation readers poll: the service's published view (<c>GET /api/state</c>) over whatever channel the
/// service answers on. The in-mod player API implements it; the lifecycle only needs the HTTP status.
/// </summary>
public interface IServiceStateSource
{
    /// <summary>Reads the published state once and returns the HTTP status code; throws when the request itself fails.</summary>
    Task<int> GetStateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The observation-load row's concurrent readers (Go <c>ObservationReaders</c>): <c>count</c> clients each polling
/// the state every <c>interval</c> for the whole row, the way several open clients read the service's published view.
/// They add read pressure beside the controller's own reads and never change anything. Latencies are recorded so a
/// reader starved by the controller (or the controller by them) is visible in the row.
/// </summary>
public sealed class ObservationReaders : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancel;
    private readonly List<Task> _tasks = [];
    private readonly Lock _gate = new();
    private readonly List<double> _latencies = [];
    private readonly int _count;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ulong _reads;
    private ulong _errors;

    private ObservationReaders(int count, CancellationTokenSource cancel)
    {
        _count = count;
        _cancel = cancel;
    }

    /// <summary>Starts <paramref name="count"/> readers against the service.</summary>
    public static ObservationReaders Start(IServiceStateSource service, int count, TimeSpan interval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        var readers = new ObservationReaders(count, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        for (var i = 0; i < count; i++)
        {
            readers._tasks.Add(Task.Run(() => readers.ReadLoopAsync(service, interval, readers._cancel.Token), CancellationToken.None));
        }

        return readers;
    }

    /// <summary>Ends the readers and summarizes what they read.</summary>
    public async Task<JsonObject> StopAsync()
    {
        await _cancel.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_tasks).ConfigureAwait(false);
        double[] latencies;
        ulong reads;
        ulong errors;
        lock (_gate)
        {
            latencies = [.. _latencies];
            reads = _reads;
            errors = _errors;
        }

        Array.Sort(latencies);
        return new JsonObject
        {
            ["readers"] = _count,
            ["reads"] = reads,
            ["errors"] = errors,
            ["duration_s"] = _clock.Elapsed.TotalSeconds,
            ["read_p50_ms"] = Rank(latencies, 0.50),
            ["read_p95_ms"] = Rank(latencies, 0.95),
            ["read_max_ms"] = Rank(latencies, 1),
        };
    }

    /// <summary>Refuses a readers summary that shows no load was applied: a row claiming observation load must have read successfully.</summary>
    public static IReadOnlyList<string> ReaderProblems(JsonObject summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary["reads"].AsUInt64() is > 0
            ? []
            : [$"observation readers made no successful read ({summary["errors"].ToJson()} errors)"];
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cancel.Dispose();
    }

    private static double Rank(double[] sorted, double q)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var i = (int)((q * sorted.Length) + 0.999999) - 1;
        return sorted[Math.Clamp(i, 0, sorted.Length - 1)];
    }

    private async Task ReadLoopAsync(IServiceStateSource service, TimeSpan interval, CancellationToken cancellationToken)
    {
        using var ticker = new PeriodicTimer(interval);
        try
        {
            while (await ticker.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var began = Stopwatch.StartNew();
                var ok = false;
                try
                {
                    ok = await service.GetStateAsync(cancellationToken).ConfigureAwait(false) == 200;
                }
                catch (HttpRequestException)
                {
                }
                catch (AcceptanceException)
                {
                }

                var ms = began.Elapsed.TotalMilliseconds;
                lock (_gate)
                {
                    if (ok)
                    {
                        _reads++;
                        _latencies.Add(ms);
                    }
                    else
                    {
                        _errors++;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop ends the readers.
        }
    }
}
