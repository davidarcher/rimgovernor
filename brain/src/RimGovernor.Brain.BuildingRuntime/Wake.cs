using System.Threading.Channels;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Names one attempt whose terminal outcome the native clock latched, so the Worker reconciles that attempt next
/// instead of rotating.
/// </summary>
public readonly record struct WakeOutcome(ActionId Action, AttemptId Attempt, bool Terminal = false);

/// <summary>
/// Carries committed clock evidence from the poll loop to the scheduler step loop and the Worker. Only evidence
/// the journal has stored wakes anyone; a wake is a hint to act now, never a fact by itself. A caller that has no
/// signal holds null and uses <see cref="Wait"/>, which never completes for it.
/// </summary>
public sealed class WakeSignal
{
    private readonly object _mu = new();
    private readonly Channel<bool> _ch = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private Dictionary<ActionId, WakeOutcome> _pending = [];
    private readonly HashSet<FactFamily> _families = [];

    // The store sections the pages' invalidations made stale, as narrowed as native attributed them; the
    // families stay the byte cache's whole-family view of the same events.
    private readonly HashSet<Facts.Section> _sections = [];
    private bool _authority;

    // The native stamp of the earliest stop still pending for the step loop, null when the page carried none;
    // the step that acts on it publishes the stop-to-step latency.
    private DateTimeOffset? _stopAt;

    // Counts committed stops; _stopsTaken is the count the step loop last drained, so a step reason reports
    // whether a stop is pending. No admission waits for a stop: every kind the Worker dispatches is validated
    // natively at apply time and dispatches under a running window as readily as between windows.
    private ulong _stops;

    /// <summary>Merges outcomes into the pending set and signals without blocking.</summary>
    public void Notify(IReadOnlyList<WakeOutcome> outcomes, bool authority) =>
        NotifyInvalidated(outcomes, [], authority);

    /// <summary>Notify carrying the fact families the committed page's ObservationInvalidated events named as well.</summary>
    public void NotifyInvalidated(IReadOnlyList<WakeOutcome> outcomes, IReadOnlyList<FactFamily> families, bool authority) =>
        NotifyStopped(outcomes, families, authority, false);

    /// <summary>NotifyInvalidated that also records whether the page stopped the clock.</summary>
    public void NotifyStopped(IReadOnlyList<WakeOutcome> outcomes, IReadOnlyList<FactFamily> families, bool authority, bool stopped) =>
        NotifyStopAt(outcomes, families, authority, stopped, null);

    /// <summary>NotifyStopped carrying the native stamp of the stop (the Stopped event's observed_at), null when unknown.</summary>
    public void NotifyStopAt(IReadOnlyList<WakeOutcome> outcomes, IReadOnlyList<FactFamily> families, bool authority, bool stopped, DateTimeOffset? stopAt) =>
        NotifySections(outcomes, families, [], authority, stopped, stopAt);

    /// <summary>
    /// NotifyStopAt carrying the sections the page's invalidations narrowed to (ClockPageSections).
    /// Invalidations coalesce into one set; a terminal outcome is never replaced by a later non-terminal one of
    /// the same attempt, and authority stays set until taken.
    /// </summary>
    public void NotifySections(IReadOnlyList<WakeOutcome> outcomes, IReadOnlyList<FactFamily> families, IReadOnlyList<Facts.Section> sections, bool authority, bool stopped, DateTimeOffset? stopAt)
    {
        lock (_mu)
        {
            foreach (var o in outcomes)
            {
                if (_pending.TryGetValue(o.Action, out var held) && held.Terminal && !o.Terminal)
                {
                    continue;
                }
                _pending[o.Action] = o;
            }
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

    /// <summary>Drains the pending outcomes and the authority flag; the invalidated families are left for <see cref="TakeInvalidated"/>.</summary>
    public (IReadOnlyDictionary<ActionId, WakeOutcome> Outcomes, bool Authority) Take()
    {
        lock (_mu)
        {
            var outcomes = _pending;
            var authority = _authority;
            _pending = [];
            _authority = false;
            return (outcomes, authority);
        }
    }

    /// <summary>Drains the pending outcomes, invalidated families, the authority flag and the pending stop into one step reason.</summary>
    public StepReason TakeInvalidated()
    {
        lock (_mu)
        {
            var reason = new StepReason
            {
                Cause = StepCause.Wake,
                Events = [.. _pending.Values.OrderBy(o => o.Action)],
                Families = [.. _families.Order()],
                Sections = [.. _sections.Order()],
                Authority = _authority,
            };
            _pending = [];
            _families.Clear();
            _sections.Clear();
            _authority = false;
            return reason;
        }
    }
}

/// <summary>What one committed clock page says that should wake the step loop (Go's five return values of clockPageWakeStopped).</summary>
internal sealed record ClockPageWake(IReadOnlyList<WakeOutcome> Outcomes, IReadOnlyList<FactFamily> Families, bool Authority, bool Stopped, DateTimeOffset? StopAt);

internal static partial class Pkg
{
    /// <summary>
    /// Reads the wake-relevant facts out of one committed page: the latched outcomes, the families its
    /// ObservationInvalidated events named, whether authority changed, and whether the page carried a Stopped
    /// event with the earliest such event's native stamp (null when the event carried none).
    /// </summary>
    internal static ClockPageWake ClockPageWakeStopped(ClockPb.EventsPage page)
    {
        var outcomes = new List<WakeOutcome>();
        var families = new List<FactFamily>();
        var seen = new HashSet<FactFamily>();
        var authority = false;
        var stopped = false;
        DateTimeOffset? stopAt = null;
        foreach (var @event in page.Events)
        {
            switch (@event.Event)
            {
                case ClockPb.ClockEventEvent.OperationOutcomeCase { Value: var operationOutcome }:
                    outcomes.Add(WakeOutcomeOf(operationOutcome));
                    break;
                case ClockPb.ClockEventEvent.StoppedCase { Value: var stoppedEvent }:
                    if (Sto.ClockJournal.OtherWorld(@event, page))
                    {
                        continue; // a previous load's stop
                    }
                    stopped = true;
                    if (@event.ObservedAtUnixMs is not null && @event.ObservedAtUnixMs.GetValueOrDefault() > 0)
                    {
                        var at = DateTimeOffset.FromUnixTimeMilliseconds(@event.ObservedAtUnixMs.GetValueOrDefault());
                        if (stopAt is null || at < stopAt)
                        {
                            stopAt = at;
                        }
                    }
                    if (stoppedEvent.Evidence?.Watch is { Outcome: { } watchOutcome })
                    {
                        outcomes.Add(WakeOutcomeOf(watchOutcome));
                    }
                    break;
                case ClockPb.ClockEventEvent.AuthorityChangedCase:
                    authority = true;
                    break;
                case ClockPb.ClockEventEvent.ObservationInvalidatedCase { Value: var invalidated }:
                    foreach (var wire in invalidated.Families)
                    {
                        if (!BridgeApi.FactFamilyFromWire(wire, out var family))
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
        return new ClockPageWake(outcomes, families, authority, stopped, stopAt);
    }

    /// <summary>
    /// Names the store sections the page's ObservationInvalidated events made stale
    /// (Facts.Invalidation.Sections): the routing key for the planners a wake re-runs. An event whose family
    /// the controller does not know names nothing here; the wake's authority flag (ClockPageWakeStopped)
    /// already re-runs everything.
    /// </summary>
    internal static IReadOnlyList<Facts.Section> ClockPageSections(ClockPb.EventsPage page)
    {
        var output = new List<Facts.Section>();
        var seen = new HashSet<Facts.Section>();
        foreach (var @event in page.Events)
        {
            if (@event.Event is not ClockPb.ClockEventEvent.ObservationInvalidatedCase { Value: var invalidated })
            {
                continue;
            }
            if (!Facts.Invalidation.TryFromWire(invalidated, out var inv))
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

    internal static WakeOutcome WakeOutcomeOf(ClockPb.OperationOutcome o)
    {
        var unknown = o.Outcome is ClockPb.OperationOutcomeOutcome.UnknownCase;
        return new WakeOutcome(new ActionId(o.Attempt?.ActionId ?? ""), new AttemptId(o.Attempt?.AttemptId ?? 0), !unknown);
    }
}
