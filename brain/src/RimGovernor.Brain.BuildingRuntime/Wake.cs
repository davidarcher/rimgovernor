using System.Threading.Channels;
using NativeEvents = RimGovernor.Brain.Bridge;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Names one attempt whose terminal outcome the native clock latched, so the Worker reconciles that attempt next
/// instead of rotating.
/// </summary>
public readonly record struct WakeOutcome(ActionId Action, AttemptId Attempt, bool Terminal = false);

/// <summary>
/// Carries clock evidence from the colony event consumer to the scheduler step loop and the Worker. Only events the
/// consumer has read against the current world wake anyone; a wake is a hint to act now, never a fact by itself. A
/// caller that has no signal holds null and uses <see cref="Wait"/>, which never completes for it.
/// </summary>
public sealed class WakeSignal
{
    private readonly object _mu = new();
    private readonly Channel<bool> _ch = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly HashSet<FactFamily> _families = [];

    // The store sections the events' invalidations made stale, as narrowed as native attributed them; the
    // families stay the byte cache's whole-family view of the same events.
    private readonly HashSet<Facts.Section> _sections = [];
    private bool _authority;

    // Set when colony events were lost: the step that takes the evidence plans Full.
    private bool _full;

    // The native stamp of the earliest stop still pending for the step loop, null when the events carried none;
    // the step that acts on it publishes the stop-to-step latency.
    private DateTimeOffset? _stopAt;

    // Counts committed stops; _stopsTaken is the count the step loop last drained, so a step reason reports
    // whether a stop is pending. No admission waits for a stop: every kind the Worker dispatches is validated
    // natively at apply time and dispatches under a running window as readily as between windows.
    private ulong _stops;

    /// <summary>Signals without blocking.</summary>
    public void Notify(bool authority) =>
        NotifyInvalidated([], authority);

    /// <summary>Notify carrying the fact families the events' ObservationInvalidated named as well.</summary>
    public void NotifyInvalidated(IReadOnlyList<FactFamily> families, bool authority) =>
        NotifyStopped(families, authority, false);

    /// <summary>NotifyInvalidated that also records whether the events stopped the clock.</summary>
    public void NotifyStopped(IReadOnlyList<FactFamily> families, bool authority, bool stopped) =>
        NotifyStopAt(families, authority, stopped, null);

    /// <summary>NotifyStopped carrying the native stamp of the stop (the Stopped event's observed_at), null when unknown.</summary>
    public void NotifyStopAt(IReadOnlyList<FactFamily> families, bool authority, bool stopped, DateTimeOffset? stopAt) =>
        NotifySections(families, [], authority, stopped, stopAt);

    /// <summary>
    /// NotifyStopAt carrying the sections the events' invalidations narrowed to (EventSections).
    /// Invalidations coalesce into one set; authority stays set until taken.
    /// </summary>
    public void NotifySections(IReadOnlyList<FactFamily> families, IReadOnlyList<Facts.Section> sections, bool authority, bool stopped, DateTimeOffset? stopAt)
    {
        lock (_mu)
        {
            foreach (var family in families)
            {
                _families.Add(family);
            }
            foreach (var section in sections)
            {
                _sections.Add(section);
            }
            _authority = _authority || authority;
            if (stopped)
            {
                _stops++;
                if (_stopAt is null || (stopAt is { } at && at < _stopAt))
                {
                    _stopAt = stopAt;
                }
            }
        }
        _ch.Writer.TryWrite(true);
    }

    /// <summary>Signals that colony events were lost: what they would have invalidated is unknown, so the next step plans Full.</summary>
    public void NotifyLost()
    {
        lock (_mu)
        {
            _full = true;
        }
        _ch.Writer.TryWrite(true);
    }

    /// <summary>
    /// Completes when a wake is signalled or <paramref name="ct"/> is cancelled. A null signal never fires, so
    /// loops that also wait on a timer need no special case.
    /// </summary>
    public static async Task Wait(WakeSignal? signal, CancellationToken ct)
    {
        if (signal is null)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return;
        }
        await signal._ch.Reader.ReadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Drains the invalidated families, the authority flag and a loss into one step reason: Full after a loss.</summary>
    public StepReason TakeInvalidated()
    {
        lock (_mu)
        {
            var reason = new StepReason
            {
                Cause = _full ? StepCause.Full : StepCause.Wake,
                Families = [.. _families.Order()],
                Sections = [.. _sections.Order()],
                Authority = _authority,
            };
            _families.Clear();
            _sections.Clear();
            _authority = false;
            _full = false;
            return reason;
        }
    }
}

/// <summary>What one batch of colony events says that should wake the step loop (Go's five return values of clockPageWakeStopped).</summary>
internal sealed record ColonyEventWake(IReadOnlyList<FactFamily> Families, bool Authority, bool Stopped, DateTimeOffset? StopAt);

partial class ColonyEventConsumer
{
    /// <summary>
    /// Reads the wake-relevant facts out of one batch of this world's events: the families its
    /// ObservationInvalidated events named, whether authority changed, and whether the batch carried a native hold
    /// with the earliest such event's native stamp.
    /// </summary>
    internal static ColonyEventWake EventsWake(IReadOnlyList<NativeEvents.ColonyEvent> events)
    {
        var families = new List<FactFamily>();
        var seen = new HashSet<FactFamily>();
        var authority = false;
        var stopped = false;
        DateTimeOffset? stopAt = null;
        foreach (var @event in events)
        {
            switch (@event)
            {
                case NativeEvents.HoldEntered held:
                    // The brain's own holds are not wakes: the driver that took them plans again itself.
                    if (!BrainRequestedHold(held.Reason))
                    {
                        stopped = true;
                        var at = DateTimeOffset.FromUnixTimeMilliseconds(held.Origin.ObservedAtUnixMs);
                        if (stopAt is null || at < stopAt)
                        {
                            stopAt = at;
                        }
                    }
                    break;
                case NativeEvents.AuthorityChanged:
                    authority = true;
                    break;
                case NativeEvents.ObservationInvalidated invalidated:
                    foreach (var wire in invalidated.Families)
                    {
                        if (!Families.FactFamilyFromWire(wire, out var family))
                        {
                            // An unknown family can be anything: an authority-sized wake.
                            authority = true;
                            continue;
                        }
                        if (seen.Add(family))
                        {
                            families.Add(family);
                        }
                    }
                    break;
            }
        }
        return new ColonyEventWake(families, authority, stopped, stopAt);
    }

    /// <summary>
    /// Names the store sections the batch's ObservationInvalidated events made stale
    /// (Facts.Invalidation.Sections): the routing key for the planners a wake re-runs. An event whose family
    /// the controller does not know names nothing here; the wake's authority flag (EventsWake)
    /// already re-runs everything.
    /// </summary>
    internal static IReadOnlyList<Facts.Section> EventSections(IReadOnlyList<NativeEvents.ColonyEvent> events)
    {
        var output = new List<Facts.Section>();
        var seen = new HashSet<Facts.Section>();
        foreach (var @event in events)
        {
            if (@event is not NativeEvents.ObservationInvalidated invalidated || !TryInvalidation(invalidated, out var inv))
            {
                continue;
            }
            foreach (var section in inv.Sections())
            {
                if (seen.Add(section))
                {
                    output.Add(section);
                }
            }
        }
        return output;
    }

    // The store's view of one invalidation; an unknown family reports false.
    private static bool TryInvalidation(NativeEvents.ObservationInvalidated value, out Facts.Invalidation invalidation)
    {
        var families = new List<FactFamily>();
        foreach (var wire in value.Families)
        {
            if (!Families.FactFamilyFromWire(wire, out var family))
            {
                invalidation = new Facts.Invalidation();
                return false;
            }
            families.Add(family);
        }
        FactRect? rect = value.Cells is { } cells ? new FactRect(cells.MinX, cells.MinZ, cells.MaxX, cells.MaxZ) : null;
        invalidation = new Facts.Invalidation { Families = EquatableArray.From(families), Ids = EquatableArray.From(value.EntityIds), Rect = rect };
        return true;
    }

    /// <summary>Go <c>OtherWorld</c>: whether the event was observed in another world (colony, load or map) than <paramref name="current"/>, the world loaded now.</summary>
    internal static bool OtherWorld(NativeEvents.ColonyEvent @event, CommonPb.Identity? current)
    {
        if (current is null)
        {
            return false;
        }
        var a = @event.Origin;
        return !string.Equals(a.ColonyId, current.ColonyId ?? "", StringComparison.Ordinal) || !string.Equals(a.LoadToken, current.LoadToken ?? "", StringComparison.Ordinal) || a.MapId != current.MapId;
    }

    /// <summary>Whether the hold was the brain's own request (a coupled order, a review or an autosave) rather than native's.</summary>
    internal static bool BrainRequestedHold(ClockPb.HoldReason reason) =>
        reason is ClockPb.HoldReason.CoupledOrder or ClockPb.HoldReason.Review or ClockPb.HoldReason.Autosave;
}