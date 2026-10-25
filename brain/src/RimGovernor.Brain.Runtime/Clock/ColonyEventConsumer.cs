using System.Threading.Channels;
using NativeEvents = RimGovernor.Brain.Bridge;

namespace RimGovernor.Brain.Runtime;

// The brain end of the colony events native pushes (the native-owned queue behind IGameApi.OpenColonyEventsAsync).
// It wakes the step loop on a hold entry or an authority change, marks the planners an invalidation dirties, and checks
// authority; nothing about events is kept. It never disables authority for a hold and never releases one: native holds
// itself on a hazard or the review tick and stays held until the run driver sends a Run whose hold serial matches.

/// <summary>What the native clock exposes to the colony event consumer.</summary>
public interface IColonyEventNative
{
    /// <summary>One bare or identified step read; the consumer uses it only for the current world's context.</summary>
    Task<RimGovernor.Brain.GameFacts.Observations.BundleSnapshot?> ReadStepAsync(StepRequest request, CancellationToken ct);

    /// <summary>Opens a fresh, empty native queue and returns its reading side; the loss counter restarts at zero and the previous reader completes.</summary>
    Task<ChannelReader<NativeEvents.ColonyEvent>> OpenColonyEventsAsync(CancellationToken ct);

    /// <summary>The events native dropped since the open; any change forces a Full step.</summary>
    long ColonyEventsLost { get; }
}

/// <summary>
/// Reads the colony events native pushes, from an open that drops what was queued before it: a started or restarted
/// brain replays no backlog, and its first step plans Full. Each batch is read against the current world's context:
/// an event from another world is ignored, and a context that does not match the held authority ends it. A change in
/// native's loss counter wakes a Full step; a batch whose context read failed is held and read again. It never takes the
/// player gate.
/// </summary>
internal sealed partial class ColonyEventConsumer(Session session, IColonyEventNative native, WakeSignal? wake, ClockRunConfig config)
{
    // One native queue's worth (ColonyEventChannel.Capacity): a batch never grows past what native can hold.
    private const int BatchLimit = 1024;

    /// <summary>The event row a loss writes (otel-signals.md): native dropped events.</summary>
    internal const string ColonyEventsLostKind = "colony_events_lost";

    /// <summary>The event row a failed context read writes (otel-signals.md): the batch is held and read again.</summary>
    internal const string ColonyEventsDelayedKind = "colony_events_delayed";

    private static readonly TimeSpan BackoffMin = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan BackoffMax = TimeSpan.FromSeconds(2);

    private RimGovernor.Brain.GameFacts.Common.Identity? _identity;
    private int _opened;
    private long _seen;

    /// <summary>The loaded world's identity as of the last context the consumer read; null before one, or after a heartbeat found it stale.</summary>
    public RimGovernor.Brain.GameFacts.Common.Identity? Identity => Volatile.Read(ref _identity);

    /// <summary>Whether the native queue is open: before it, no event reaches the brain and a held clock does not resume.</summary>
    public bool Opened => Volatile.Read(ref _opened) != 0;

    /// <summary>
    /// The evidence the consumer has delivered: one per event of this world and one per loss it saw (the tests wait on it).
    /// </summary>
    public long Seen => Interlocked.Read(ref _seen);

    /// <summary>The identity for the heartbeat: the last one read, or a fresh read when none is known.</summary>
    public async Task<RimGovernor.Brain.GameFacts.Common.Identity> IdentityAsync(CancellationToken ct)
    {
        if (Identity is { } known)
        {
            return known;
        }
        return (await CurrentContextAsync(ct).ConfigureAwait(false)).Identity!;
    }

    /// <summary>Drops the known identity (a heartbeat refused it); the next <see cref="IdentityAsync"/> reads it again.</summary>
    public void ForgetIdentity() => Volatile.Write(ref _identity, null);

    /// <summary>Opens the queue and delivers its events until <paramref name="ct"/> ends.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        ChannelReader<NativeEvents.ColonyEvent>? reader = null;
        long lost = 0;
        var backoff = TimeSpan.Zero;
        var batch = new List<NativeEvents.ColonyEvent>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (reader is null)
                {
                    reader = await native.OpenColonyEventsAsync(ct).ConfigureAwait(false);
                    lost = native.ColonyEventsLost;
                    Volatile.Write(ref _opened, 1);
                }
                if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    // A newer open completed this queue (a restarted or reloaded brain took it over): shutdown, not a loss.
                    return;
                }
                batch.Clear();
                while (batch.Count < BatchLimit && reader.TryRead(out var next))
                {
                    batch.Add(next);
                }
                var now = native.ColonyEventsLost;
                var lossChanged = now != lost;
                lost = now;
                await DeliverAsync(batch, lossChanged, ct).ConfigureAwait(false);
                backoff = TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // A failed open backs off from 250 ms to 2 s.
                backoff = Backoff(backoff);
                try
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static TimeSpan Backoff(TimeSpan previous) =>
        TimeSpan.FromTicks(Math.Min(BackoffMax.Ticks, Math.Max(BackoffMin.Ticks, previous.Ticks * 2)));

    private async Task<RimGovernor.Brain.GameFacts.Common.ObservationContext> CurrentContextAsync(CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(config.CallTimeout);
        var snapshot = await native.ReadStepAsync(new StepRequest(), bounded.Token).ConfigureAwait(false)
            ?? throw new HeldException("events context read returned nothing");
        var context = snapshot.Context ?? throw new BridgeContractException("events context read carried no context");
        _ = context.Identity ?? throw new BridgeContractException("events context read carried no identity");
        Volatile.Write(ref _identity, context.Identity);
        return context;
    }

    private static bool MatchesAuthority(RimGovernor.Brain.GameFacts.Common.ObservationContext observed, ControlState state) =>
        !state.Enabled
        || (state.ObservationKnown && Equals(observed.Identity, Boundary.Identity(state.Snapshot)) && observed.NativeGeneration.IsAt((ulong)state.Snapshot.Native.Value));

    // Delivers one batch against the current context. A context read that fails (a game thread busy past the call bound)
    // is retried with the batch held: the batch is delayed, not lost, so it wakes no Full step. Native counts what the
    // queue drops meanwhile, and that loss still does.
    private async Task DeliverAsync(List<NativeEvents.ColonyEvent> batch, bool lossChanged, CancellationToken ct)
    {
        RimGovernor.Brain.GameFacts.Common.ObservationContext current;
        var backoff = TimeSpan.Zero;
        while (true)
        {
            try
            {
                current = await CurrentContextAsync(ct).ConfigureAwait(false);
                break;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                ForgetIdentity();
                backoff = Backoff(backoff);
                ClockEvents.Event("clock-scheduler", ColonyEventsDelayedKind, "colony events context read failed; the batch is read again",
                    ("events", batch.Count), ("err", e.Message), ("retry_ms", backoff.TotalMilliseconds));
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
        }
        var state = session.State();
        if (!MatchesAuthority(current, state))
        {
            // The loaded world is not the one authority was granted for: authority ends, the batch is still read.
            ClockEvents.AuthorityLost("colony events context does not match the held authority",
                ("held_known", state.ObservationKnown), ("held", state.Snapshot.ToString()), ("observed_generation", current.NativeGeneration), ("observed_tick", current.Tick));
            if (!TryDisableObserved(state))
            {
                ClockEvents.Event("clock-scheduler", "authority", "authority changed under the colony events");
            }
        }
        var events = batch.Where(e => !ColonyEventConsumer.OtherWorld(e, current.Identity)).ToList();
        if (lossChanged)
        {
            Lost(("lost", native.ColonyEventsLost));
        }
        if (events.Count == 0)
        {
            return;
        }
        Tel.ObserveTick(current.Tick);
        var found = ColonyEventConsumer.EventsWake(events);
        wake?.NotifySections(found.Families, ColonyEventConsumer.EventSections(events), found.Authority, found.Stopped, found.StopAt, found.Review, found.Speed);
        Interlocked.Add(ref _seen, events.Count);
    }

    // Events the brain did not receive: the next step plans Full, and the loss is evidence a held step has not seen.
    private void Lost(params (string Key, object? Value)[] attrs)
    {
        ClockEvents.Event("clock-scheduler", ColonyEventsLostKind, "colony events were lost; the next step plans Full", attrs);
        wake?.NotifyLost();
        Interlocked.Increment(ref _seen);
    }

    // Disables authority only if it still is the state the consumer observed; false when it changed under it.
    private bool TryDisableObserved(ControlState observed)
    {
        try
        {
            session.DisableObserved(observed);
            return true;
        }
        catch (ConflictException)
        {
            return false;
        }
    }
}
