using System.Collections.Concurrent;
using System.Threading.Channels;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Host;

/// <summary>
/// Asks the prediction service about hunt rosters in the background, one request at a time (the service takes one),
/// and remembers each answer as a roster estimate or a refusal. It holds one comparison: the anchor read policy asked
/// with, and the answers built from it. Policy sees only <see cref="HuntEstimateView"/> and judges whether the anchor
/// still describes the colony (#3525); a roster still being predicted, refused, or unanswered has no estimate and is
/// never treated as safe.
/// </summary>
public sealed class HuntEstimateService : IHuntEstimates, IAsyncDisposable
{
    public const int MaxPending = 64;
    /// <summary>How long a refusal that says nothing about the roster is remembered before it is asked again.</summary>
    public const long RetryTicks = GameTime.TicksPerHour;
    private sealed record Entry(HuntRoster Roster, int Comparison, long Tick, HuntEstimate? Estimate, string Refusal, long Until);
    private readonly EncounterPredictionConsumer _consumer;
    private readonly ConcurrentDictionary<string, Entry> _done = new(StringComparer.Ordinal);
    // Scenes queued or in flight, each with the comparison it was queued for: a scene of a replaced comparison is queued
    // again for the new one at once, and its old item is skipped when the loop reaches it.
    private readonly ConcurrentDictionary<string, int> _pending = new(StringComparer.Ordinal);
    private readonly Channel<(HuntActivityRead Anchor, int Comparison, HuntRoster Roster, long Tick)> _queue =
        Channel.CreateBounded<(HuntActivityRead, int, HuntRoster, long)>(new BoundedChannelOptions(MaxPending) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly IAsyncDisposable? _owned;
    // The current comparison: the read its scenes are built from and its number, which its entries carry. The planner
    // thread writes both; the loop reads the number to skip a scene whose comparison was replaced while it waited.
    private volatile HuntActivityRead? _anchor;
    private volatile int _comparison;

    /// <summary>The prediction worker assembly (RimGovernor.RealCombat.Host.dll) the launcher built; unset means no estimates.</summary>
    public const string WorkerVariable = "RIMGOVERNOR_ENCOUNTER_WORKER";

    /// <summary>The dotnet executable that runs the worker; default <c>dotnet</c> on the path.</summary>
    public const string DotnetVariable = "RIMGOVERNOR_ENCOUNTER_DOTNET";

    /// <summary>
    /// Builds the service over a local worker process for the observed engine identity, or says why not: no worker
    /// configured, or a configured path that is not a file. Without a service every hunt roster stays unestimated.
    /// </summary>
    public static HuntEstimateService? Create(Func<string, string?> get, string gameBuild, string mods, string predictorMods, string gameDirectory, string workingDirectory, out string reason)
    {
        var worker = get(WorkerVariable);
        if (string.IsNullOrWhiteSpace(worker)) { reason = WorkerVariable + " is not set"; return null; }
        worker = worker.Trim();
        if (!Path.IsPathRooted(worker) || !File.Exists(worker)) { reason = WorkerVariable + " is not an existing absolute file path"; return null; }
        var dotnet = get(DotnetVariable);
        var options = new LocalEncounterOptions(string.IsNullOrWhiteSpace(dotnet) ? "dotnet" : dotnet.Trim(), worker, gameDirectory, workingDirectory, gameBuild, mods, predictorMods,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(2), HuntEncounterRequests.Samples, HuntEncounterRequests.Horizon, 128, 32, 1048576);
        reason = "";
        var predictor = new LocalEncounterPredictor(options);
        return new HuntEstimateService(new EncounterPredictionConsumer(predictor, TimeSpan.FromSeconds(95)), predictor);
    }

    public HuntEstimateService(EncounterPredictionConsumer consumer, IAsyncDisposable? owned = null)
    {
        _consumer = consumer;
        _owned = owned;
        _loop = Task.Run(RunAsync);
    }

    public HuntEstimateView Current(long tick)
    {
        var estimates = new Dictionary<string, HuntEstimate>(StringComparer.Ordinal);
        var refusals = new Dictionary<string, string>(StringComparer.Ordinal);
        var comparison = _comparison;
        foreach (var (key, entry) in _done)
        {
            if (!Fresh(entry, comparison, tick)) continue;
            if (entry.Estimate is { } estimate) estimates[key] = estimate; else refusals[key] = entry.Refusal;
        }
        return new(_anchor, estimates, refusals, _pending.Where(p => p.Value == comparison).Select(p => p.Key).ToHashSet(StringComparer.Ordinal));
    }

    public int Request(HuntActivityRead anchor, IEnumerable<HuntRoster> rosters, long tick)
    {
        if (!ReferenceEquals(anchor, _anchor))
        {
            // A new comparison: its scenes are built from this read, and the old one's answers, built from another, are not comparable with them.
            _anchor = anchor;
            _comparison++;
            _done.Clear();
        }
        var comparison = _comparison;
        var queued = 0;
        foreach (var roster in rosters)
        {
            // Keyed by scene: a hauler's roster shares its gunners' scene and is never asked twice.
            if (_done.TryGetValue(roster.SceneKey, out var entry) && Fresh(entry, comparison, tick)) continue;
            if (_pending.TryGetValue(roster.SceneKey, out var queuedFor) && queuedFor == comparison) continue;
            _pending[roster.SceneKey] = comparison;
            if (_queue.Writer.TryWrite((anchor, comparison, roster, tick))) queued++; else _pending.TryRemove(new KeyValuePair<string, int>(roster.SceneKey, comparison));
        }
        return queued;
    }

    private static bool Fresh(Entry entry, int comparison, long tick) => entry.Comparison == comparison && tick <= entry.Until && tick >= entry.Tick;

    private async Task RunAsync()
    {
        // Disposal completes the writer and cancels the in-flight prediction, which returns a cancelled status rather than throwing.
        while (!_stop.IsCancellationRequested && await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            while (!_stop.IsCancellationRequested && _queue.Reader.TryRead(out var item))
            {
                try
                {
                    // A scene of a comparison replaced while it waited is not worth the worker's time: the new comparison asks for it again.
                    if (item.Comparison != _comparison) continue;
                    var entry = await PredictAsync(item.Anchor, item.Comparison, item.Roster, item.Tick).ConfigureAwait(false);
                    if (item.Comparison == _comparison) _done[item.Roster.SceneKey] = entry;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A failed prediction is a refusal to try again soon, never an answer and never the end of the loop.
                    _done[item.Roster.SceneKey] = new(item.Roster, item.Comparison, item.Tick, null, "prediction_failed:" + e.GetType().Name + ":" + e.Message, item.Tick + RetryTicks);
                }
                finally { _pending.TryRemove(new KeyValuePair<string, int>(item.Roster.SceneKey, item.Comparison)); }
            }
    }

    private async Task<Entry> PredictAsync(HuntActivityRead anchor, int comparison, HuntRoster roster, long tick)
    {
        var (request, refusal) = HuntEncounterRequests.Build(anchor, roster, tick);
        if (request is null) return new(roster, comparison, tick, null, refusal, tick + HuntEncounterRequests.Lifetime);
        var prediction = await _consumer.PredictAsync(request, () => request, _stop.Token).ConfigureAwait(false);
        var (estimate, why) = HuntAlternatives.EstimateOf(anchor, roster, request, prediction);
        // A busy, cancelled, timed-out or unavailable service says nothing about the roster: ask again soon, not tomorrow.
        var transient = prediction.Status is EncounterStatus.Busy or EncounterStatus.Cancelled or EncounterStatus.TimedOut or EncounterStatus.Stale or EncounterStatus.Unavailable;
        return new(roster, comparison, tick, estimate, why, tick + (transient ? RetryTicks : HuntEncounterRequests.Lifetime));
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
        if (_owned is not null) await _owned.DisposeAsync().ConfigureAwait(false);
    }
}
