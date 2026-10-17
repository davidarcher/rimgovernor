using System.Collections.Concurrent;
using System.Threading.Channels;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Host;

/// <summary>
/// Asks the prediction service about hunt rosters in the background, one request at a time (the service takes one),
/// and remembers each answer as a roster estimate or a refusal. Policy sees only <see cref="HuntEstimateView"/>:
/// a roster still being predicted, refused, or unanswered has no estimate and is never treated as safe.
/// </summary>
public sealed class HuntEstimateService : IHuntEstimates, IAsyncDisposable
{
    public const int MaxPending = 64;
    /// <summary>How long a refusal that says nothing about the roster is remembered before it is asked again.</summary>
    public const long RetryTicks = GameTime.TicksPerHour;
    private sealed record Entry(HuntActivityRead Made, long Tick, HuntEstimate? Estimate, string Refusal, long Until);
    private readonly EncounterPredictionConsumer _consumer;
    private readonly ConcurrentDictionary<string, Entry> _done = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly Channel<(HuntActivityRead Read, HuntRoster Roster, long Tick)> _queue =
        Channel.CreateBounded<(HuntActivityRead, HuntRoster, long)>(new BoundedChannelOptions(MaxPending) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly IAsyncDisposable? _owned;

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

    public HuntEstimateView Current(HuntActivityRead read, long tick)
    {
        var estimates = new Dictionary<string, HuntEstimate>(StringComparer.Ordinal);
        var refusals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, entry) in _done)
        {
            if (!Fresh(entry, read, tick, key)) continue;
            if (entry.Estimate is { } estimate) estimates[key] = estimate; else refusals[key] = entry.Refusal;
        }
        return new(estimates, refusals, new HashSet<string>(_pending.Keys, StringComparer.Ordinal));
    }

    public int Request(HuntActivityRead read, IEnumerable<HuntRoster> rosters, long tick)
    {
        var queued = 0;
        foreach (var roster in rosters)
        {
            if (_done.TryGetValue(roster.Key, out var entry) && Fresh(entry, read, tick, roster.Key)) continue;
            if (!_pending.TryAdd(roster.Key, 0)) continue;
            if (_queue.Writer.TryWrite((read, roster, tick))) queued++; else _pending.TryRemove(roster.Key, out _);
        }
        return queued;
    }

    private static bool Fresh(Entry entry, HuntActivityRead read, long tick, string key)
    {
        if (tick > entry.Until || tick < entry.Tick) return false;
        var roster = ParseKey(key);
        return HuntEncounterRequests.StillDescribes(entry.Made, read, roster);
    }

    private static HuntRoster ParseKey(string key)
    {
        var split = key.Split('>');
        return new(EquatableArray.From(split[0].Split('+')), EquatableArray.From(split[1].Split('+')));
    }

    private async Task RunAsync()
    {
        // Disposal completes the writer and cancels the in-flight prediction, which returns a cancelled status rather than throwing.
        while (!_stop.IsCancellationRequested && await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            while (!_stop.IsCancellationRequested && _queue.Reader.TryRead(out var item))
            {
                try { _done[item.Roster.Key] = await PredictAsync(item.Read, item.Roster, item.Tick).ConfigureAwait(false); }
                finally { _pending.TryRemove(item.Roster.Key, out _); }
            }
    }

    private async Task<Entry> PredictAsync(HuntActivityRead read, HuntRoster roster, long tick)
    {
        var (request, refusal) = HuntEncounterRequests.Build(read, roster, tick);
        if (request is null) return new(read, tick, null, refusal, tick + HuntEncounterRequests.Lifetime);
        var prediction = await _consumer.PredictAsync(request, () => request, _stop.Token).ConfigureAwait(false);
        var (estimate, why) = HuntAlternatives.EstimateOf(read, roster, request, prediction);
        // A busy, cancelled, timed-out or unavailable service says nothing about the roster: ask again soon, not tomorrow.
        var transient = prediction.Status is EncounterStatus.Busy or EncounterStatus.Cancelled or EncounterStatus.TimedOut or EncounterStatus.Stale or EncounterStatus.Unavailable;
        return new(read, tick, estimate, why, tick + (transient ? RetryTicks : HuntEncounterRequests.Lifetime));
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
