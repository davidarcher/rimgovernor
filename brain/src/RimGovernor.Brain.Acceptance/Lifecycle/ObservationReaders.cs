using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

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
    public static ObservationReaders Start(ServiceProcess service, int count, TimeSpan interval, CancellationToken cancellationToken)
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
        return (JsonObject)OutputJson.Node(
            new ReadersSummary
            {
                Readers = _count,
                Reads = reads,
                Errors = errors,
                DurationS = _clock.Elapsed.TotalSeconds,
                ReadP50Ms = Rank(latencies, 0.50),
                ReadP95Ms = Rank(latencies, 0.95),
                ReadMaxMs = Rank(latencies, 1),
            },
            HarnessRowJsonContext.Default.ReadersSummary);
    }

    /// <summary>Refuses a readers summary that shows no load was applied: a row claiming observation load must have read successfully.</summary>
    public static IReadOnlyList<string> ReaderProblems(JsonObject summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var read = summary.Deserialize(HarnessRowJsonContext.Default.ReadersSummary)!;
        return read.Reads > 0
            ? []
            : [string.Create(CultureInfo.InvariantCulture, $"observation readers made no successful read ({read.Errors} errors)")];
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

    private async Task ReadLoopAsync(ServiceProcess service, TimeSpan interval, CancellationToken cancellationToken)
    {
        using var pace = Wait.Pace(interval);
        try
        {
            while (true)
            {
                await pace.NextAsync(cancellationToken).ConfigureAwait(false);
                var began = Stopwatch.StartNew();
                var ok = false;
                try
                {
                    ok = (await service.ApiAsync("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false)).Status == 200;
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
