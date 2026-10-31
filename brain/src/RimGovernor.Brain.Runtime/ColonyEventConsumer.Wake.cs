using System.Threading.Channels;
using NativeEvents = RimGovernor.Brain.Bridge;

namespace RimGovernor.Brain.Runtime;

/// <summary>
/// Names one attempt whose terminal outcome the native clock latched, so the Worker reconciles that attempt next
/// instead of rotating.
/// </summary>
public readonly record struct WakeOutcome(ActionId Action, AttemptId Attempt, bool Terminal = false);

/// <summary>
/// Carries clock evidence from the colony event consumer to the scheduler step loop and the Worker. Only events the
/// consumer has read against the current world wake anyone; a wake is a hint to act now, never a fact by itself. A
/// caller that has no signal holds null and uses <see cref="Wait"/>, which never completes for it.
/// The step loop and the Worker wait on separate channels: the Worker is hinted by everything, the step loop only by
/// a hold entry, an authority change, a player speed request or lost events (<see cref="WaitStep"/>).
/// Invalidations and planner marks only accumulate for the step that the next hold wakes: they wake no step while the clock runs (#3556).
/// </summary>
public sealed class WakeSignal
{
    private readonly object _mu = new();
    private readonly Channel<bool> _step = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<bool> _hint = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly HashSet<FactFamily> _families = [];

    // The store sections the events' invalidations made stale, as narrowed as native attributed them; the
    // families stay the byte cache's whole-family view of the same events.
    private readonly HashSet<Facts.Section> _sections = [];
    private bool _authority;

    // Set when the player asked for a speed in game: the step and the Run that follows it adopt the request.
    private bool _speed;

    // The planners marked due by name (NotifyPlanner), until the step loop takes them.
    private readonly HashSet<string> _planners = [];

    // Set when colony events were lost: the step that takes the evidence plans Full.
    private bool _full;

    // The native stamp of the earliest hold still pending for the step loop, null when the events carried none;
    // the step that acts on it publishes the hold-to-step latency (clock_step stop_latency_ms).
    private DateTimeOffset? _stopAt;

    // Whether a hold is pending for the step loop, and whether every pending hold was the review tick a Run named:
    // a step for the review alone runs the dirty and due planners, any other hold is an early review of everything.
    private bool _stopped;
    private bool _onlyReview = true;

    /// <summary>
    /// Records one batch of colony events: the fact families and store sections its invalidations named
    /// (EventSections), whether authority changed, whether the clock entered a hold (with the native stamp of the
    /// earliest, null when unknown) and whether every such hold was the review tick. Invalidations coalesce into one
    /// set and only hint the Worker; a hold, an authority change or a player speed request (<paramref name="speed"/>)
    /// also wakes the step loop. A speed request with no hold and no authority change is a quiet step (cause Review):
    /// the planners due at the tick and the Run that carries the adopted speed.
    /// </summary>
    public void NotifySections(IReadOnlyList<FactFamily> families, IReadOnlyList<Facts.Section> sections, bool authority, bool stopped, DateTimeOffset? stopAt, bool review, bool speed)
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
            _speed = _speed || speed;
            if (stopped)
            {
                _stopped = true;
                _onlyReview = _onlyReview && review;
                if (_stopAt is null || (stopAt is { } at && at < _stopAt))
                {
                    _stopAt = stopAt;
                }
            }
        }
        _hint.Writer.TryWrite(true);
        if (stopped || authority || speed)
        {
            _step.Writer.TryWrite(true);
        }
    }

    /// <summary>Marks the named planner due at the next step, for a change no fact family carries; a name no composed planner bears is ignored.</summary>
    public void NotifyPlanner(string planner)
    {
        lock (_mu)
        {
            _ = _planners.Add(planner);
        }
        _hint.Writer.TryWrite(true);
    }

    /// <summary>Signals that colony events were lost: what they would have invalidated is unknown, so the next step plans Full.</summary>
    public void NotifyLost()
    {
        lock (_mu)
        {
            _full = true;
        }
        _hint.Writer.TryWrite(true);
        _step.Writer.TryWrite(true);
    }

    /// <summary>
    /// Completes when any evidence is signalled (the Worker's hint) or <paramref name="ct"/> is cancelled. A null
    /// signal never fires, so loops that also wait on a timer need no special case.
    /// </summary>
    public static async Task Wait(WakeSignal? signal, CancellationToken ct)
    {
        if (signal is null)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return;
        }
        await signal._hint.Reader.ReadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Completes when a hold entry, an authority change, a player speed request or a loss is signalled (the step loop's wake) or <paramref name="ct"/> is cancelled.</summary>
    public static async Task WaitStep(WakeSignal? signal, CancellationToken ct)
    {
        if (signal is null)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return;
        }
        await signal._step.Reader.ReadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Whether a step wake is signalled right now; taking it leaves the evidence for <see cref="TakeInvalidated"/>.</summary>
    public bool TryTakeStep() => _step.Reader.TryRead(out _);

    /// <summary>
    /// Drains the evidence into one step reason: Full after a loss; Review when the only holds pending were the review
    /// tick, or no hold but a player speed request, and authority did not change; Wake otherwise. The reason carries
    /// the hold and its earliest stamp.
    /// </summary>
    public StepReason TakeInvalidated()
    {
        lock (_mu)
        {
            var reason = new StepReason
            {
                Cause = _full ? StepCause.Full : (_stopped ? _onlyReview : _speed) && !_authority ? StepCause.Review : StepCause.Wake,
                Families = [.. _families.Order()],
                Sections = [.. _sections.Order()],
                Planners = [.. _planners.Order(StringComparer.Ordinal)],
                Authority = _authority,
                Stopped = _stopped,
                StopAt = _stopAt,
            };
            _families.Clear();
            _sections.Clear();
            _planners.Clear();
            _authority = false;
            _speed = false;
            _full = false;
            _stopped = false;
            _onlyReview = true;
            _stopAt = null;
            return reason;
        }
    }
}

/// <summary>What one batch of colony events says that should wake the step loop.</summary>
internal sealed record ColonyEventWake(IReadOnlyList<FactFamily> Families, bool Authority, bool Stopped, DateTimeOffset? StopAt, bool Review, bool Speed);

partial class ColonyEventConsumer
{
    /// <summary>
    /// Reads the wake-relevant facts out of one batch of this world's events: the families its
    /// ObservationInvalidated events named, whether authority changed, and whether the batch carried a hold entry
    /// (every hold wakes the step that starts from it, the brain's own coupled-order and autosave holds included)
    /// with the earliest such event's native stamp, whether every hold was the review tick, and whether the player
    /// asked for a speed (SpeedRequested, a wake only: the request itself is read from Status).
    /// </summary>
    internal static ColonyEventWake EventsWake(IReadOnlyList<NativeEvents.ColonyEvent> events)
    {
        var families = new List<FactFamily>();
        var seen = new HashSet<FactFamily>();
        var authority = false;
        var speed = false;
        var stopped = false;
        var review = true;
        DateTimeOffset? stopAt = null;
        foreach (var @event in events)
        {
            switch (@event)
            {
                case NativeEvents.HoldEntered held:
                    stopped = true;
                    review = review && held.Reason == RimGovernor.Brain.GameFacts.Clock.HoldReason.Review;
                    var at = DateTimeOffset.FromUnixTimeMilliseconds(held.Origin.ObservedAtUnixMs);
                    if (stopAt is null || at < stopAt)
                    {
                        stopAt = at;
                    }
                    break;
                case NativeEvents.SpeedRequested:
                    speed = true;
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
        return new ColonyEventWake(families, authority, stopped, stopAt, stopped && review, speed);
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

    /// <summary>Whether the event was observed in another world (colony, load or map) than <paramref name="current"/>, the world loaded now.</summary>
    internal static bool OtherWorld(NativeEvents.ColonyEvent @event, RimGovernor.Brain.GameFacts.Common.Identity? current)
    {
        if (current is null)
        {
            return false;
        }
        var a = @event.Origin;
        return !string.Equals(a.ColonyId, current.ColonyId ?? "", StringComparison.Ordinal) || !string.Equals(a.LoadToken, current.LoadToken ?? "", StringComparison.Ordinal) || a.MapId != current.MapId;
    }
}