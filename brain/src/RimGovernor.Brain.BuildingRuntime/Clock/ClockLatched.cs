namespace RimGovernor.Brain.BuildingRuntime;

// Port of internal/buildingruntime/clock_latched.go.

/// <summary>
/// One terminal outcome the native clock latched for an attempt the journal still shows in flight (Go
/// <c>clockLatchedOutcome</c>). The scheduler defers admission while the Worker reconciles it: the stop that carried
/// the outcome wakes the Worker and the step loop alike, and a review that wins the player gate first would
/// otherwise watch the settled attempt again while its successor is still undispatched.
/// </summary>
/// <param name="Attempt">The attempt whose outcome was latched.</param>
/// <param name="Holds">The steps deferred on this outcome; past <see cref="ClockLatched.HoldMax"/> the review proceeds so a Worker that cannot reconcile the attempt never parks the clock.</param>
/// <param name="Escaped">Whether the outcome's forced pass-through was already taken.</param>
internal readonly record struct ClockLatchedOutcome(AttemptId Attempt, int Holds = 0, bool Escaped = false);

/// <summary>
/// The set of latched outcomes the Worker has yet to reconcile (Go <c>clockLatched</c>). The poll loop records them
/// as it commits a page and the step loop's reason repeats them; a step consults the set under the player gate, so
/// the poll's own write is the one that needs the lock. It also counts the holds spent on undispatched work (see
/// <see cref="Undispatched"/>).
/// </summary>
internal sealed class ClockLatched
{
    /// <summary>Go <c>clockLatchedHoldMax</c>.</summary>
    public const int HoldMax = 3;

    private readonly Lock _mu = new();
    private readonly Dictionary<ActionId, ClockLatchedOutcome> _outcomes = [];
    private readonly Dictionary<ActionId, int> _waiting = [];

    // The steps deferred in a row, over both holds and every action: past HoldMax the review proceeds however many
    // actions still owe, so a plan of several undispatchable actions (hauls the game keeps refusing) cannot park
    // the clock for the sum of their holds. A step that is not deferred resets it (Released).
    private int _deferrals;

    // Every action, of any kind, that the last Undispatched call saw pending or prepared. One of them found at a
    // later stage is the Worker's progress: the holds restart, so a plan the Worker is dispatching step by step
    // keeps the clock stopped until its last action is out instead of losing the window after three steps spent on
    // other plans' work (a 32-wall shell went out over ~19,000 ticks of a running window). An action that vanished
    // (a replaced plan) is not progress, so planner churn cannot park the clock.
    private HashSet<ActionId> _queued = [];

    // The queued actions whose forced pass-through was already reported, forgotten when the action leaves those
    // stages; _escapes is the passes not yet taken by the scheduler.
    private readonly HashSet<ActionId> _escaped = [];
    private List<ActionId> _escapes = [];

    /// <summary>Whether the native clock keeps an operation record for the kind that a watch can observe (Go <c>clockWatchedKind</c>).</summary>
    public static bool WatchedKind(ActionKind kind) => kind == ActionKind.Building;

    // Queues, once per action, the queued actions a bound just let through to review; the caller holds the lock.
    private void Escape(HashSet<ActionId> seen)
    {
        foreach (var id in seen)
        {
            if (_escaped.Add(id))
            {
                _escapes.Add(id);
            }
        }
    }

    /// <summary>The actions, in order, whose hold bound forced the review through since the last call (Go <c>takeEscapes</c>).</summary>
    public IReadOnlyList<ActionId> TakeEscapes()
    {
        lock (_mu)
        {
            var output = _escapes;
            _escapes = [];
            output.Sort();
            return output;
        }
    }

    // Records the queued set and reports whether an action the previous call saw queued has since moved on.
    private bool Progressed(IReadOnlyList<ClockWorkItem> items)
    {
        var moved = false;
        var queued = new HashSet<ActionId>();
        foreach (var item in items)
        {
            if (item.Stage is Stage.Pending or Stage.Prepared)
            {
                queued.Add(item.Action);
            }
            else if (_queued.Contains(item.Action))
            {
                moved = true;
            }
        }
        _queued = queued;
        return moved;
    }

    /// <summary>
    /// Reports, in order, the watched-kind actions still pending or prepared while none of that kind is dispatched,
    /// counting one hold on each: the successor a planner just queued (or the Worker just unblocked) has yet to be
    /// dispatched, and a window admitted first would run out its whole budget watching nothing. Each action is held
    /// at most <see cref="HoldMax"/> steps over its life, so work the Worker cannot dispatch (materials, a blocked
    /// cell) never parks the clock; the count is forgotten once the action leaves those stages.
    /// </summary>
    public IReadOnlyList<ActionId> Undispatched(IReadOnlyList<ClockWorkItem> items)
    {
        lock (_mu)
        {
            if (Progressed(items))
            {
                _deferrals = 0;
                _waiting.Clear();
                _escaped.Clear();
            }
            var waiting = new List<ActionId>();
            var seen = new HashSet<ActionId>();
            var dispatched = false;
            foreach (var item in items)
            {
                if (!WatchedKind(item.Kind))
                {
                    continue;
                }
                switch (item.Stage)
                {
                    case Stage.Dispatched or Stage.AwaitingObservation:
                        dispatched = true;
                        break;
                    case Stage.Pending or Stage.Prepared:
                        seen.Add(item.Action);
                        if (_waiting.GetValueOrDefault(item.Action) < HoldMax)
                        {
                            waiting.Add(item.Action);
                        }
                        break;
                }
            }
            foreach (var id in _waiting.Keys.ToList())
            {
                if (!seen.Contains(id))
                {
                    _waiting.Remove(id);
                }
            }
            _escaped.RemoveWhere(id => !seen.Contains(id));
            if (!dispatched && seen.Count > 0 && (waiting.Count == 0 || _deferrals >= HoldMax))
            {
                Escape(seen);
            }
            if (dispatched || waiting.Count == 0 || _deferrals >= HoldMax)
            {
                return [];
            }
            _deferrals++;
            foreach (var id in waiting)
            {
                _waiting[id] = _waiting.GetValueOrDefault(id) + 1;
            }
            waiting.Sort();
            return waiting;
        }
    }

    /// <summary>Records the terminal outcomes a wake carried (Go <c>remember</c>).</summary>
    public void Remember(IReadOnlyList<WakeOutcome> events)
    {
        lock (_mu)
        {
            foreach (var e in events)
            {
                if (e.Terminal && e.Attempt.Value != 0)
                {
                    _outcomes[e.Action] = new ClockLatchedOutcome(e.Attempt);
                }
            }
        }
    }

    /// <summary>
    /// Drops the latched outcomes the plan has absorbed (the attempt is terminal, superseded or gone) and reports,
    /// in order, those a watched attempt still owes, counting one hold on each. It reports none once an outcome has
    /// been held its bound, and none while any other watched attempt is in flight: that one is fresh to the native
    /// clock, so a window admitted now has an outcome to latch, and the settled attempt reconciles under it
    /// (Go <c>pending</c>).
    /// </summary>
    public IReadOnlyList<ActionId> Pending(IReadOnlyList<ClockWorkItem> items)
    {
        lock (_mu)
        {
            if (_outcomes.Count == 0)
            {
                return [];
            }
            var inFlight = new HashSet<ActionId>();
            var fresh = false;
            foreach (var item in items)
            {
                if (!WatchedKind(item.Kind) || item.Stage is not (Stage.Dispatched or Stage.AwaitingObservation))
                {
                    continue;
                }
                if (_outcomes.TryGetValue(item.Action, out var latched) && item.Attempt == latched.Attempt)
                {
                    inFlight.Add(item.Action);
                }
                else
                {
                    fresh = true;
                }
            }
            var pending = new List<ActionId>();
            foreach (var (id, latched) in _outcomes.ToList())
            {
                var held = inFlight.Contains(id);
                if (held && !fresh && latched.Holds >= HoldMax)
                {
                    _escapes.Add(id);
                }
                if (!held || latched.Holds >= HoldMax)
                {
                    _outcomes.Remove(id);
                    continue;
                }
                if (fresh || _deferrals >= HoldMax)
                {
                    if (!fresh && !latched.Escaped)
                    {
                        _outcomes[id] = latched with { Escaped = true };
                        _escapes.Add(id);
                    }
                    continue;
                }
                _outcomes[id] = latched with { Holds = latched.Holds + 1 };
                pending.Add(id);
            }
            if (pending.Count > 0)
            {
                _deferrals++;
            }
            pending.Sort();
            return pending;
        }
    }

    /// <summary>Records a step that was not deferred, ending the run of deferrals the bound counts (Go <c>released</c>).</summary>
    public void Released()
    {
        lock (_mu)
        {
            _deferrals = 0;
        }
    }
}
