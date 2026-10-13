namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// One planner's dependency: the open attempts of its kinds when it last ran, and the tick past which it is
/// re-evaluated regardless.
/// </summary>
internal readonly record struct PlannerWait(IReadOnlyList<ActionId> On, long Deadline);

/// <summary>
/// What a step's selection decided: whether any planner runs, the catalog filter (null for every planner), the
/// planners skipped as waiting on a dependency, and the marks the wave will clear.
/// </summary>
internal sealed record PlannerSelectionResult(bool Planners, Func<PlannerEntry, bool>? Pick, IReadOnlyList<string> Waiting, ulong Began)
{
    public static PlannerSelectionResult None { get; } = new(false, null, [], 0);
}

/// <summary>
/// The scheduler's due queue over the planner catalog: per planner, the game tick its last evaluation stands
/// until (<see cref="PlannerEntry.ReviewEvery"/>), the wake evidence it has yet to act on, and the dependency it
/// reported waiting on. A step selects the planners that are dirty or due at its tick and skips those still
/// waiting; the full step (the coarse reconciliation for missed invalidations) and an authority change run
/// everything and clear the waits. Touched only under the player gate.
/// </summary>
internal sealed class PlannerQueue
{

    // The next review tick per planner, recorded when it ran; a planner absent from it has not run since the
    // last full step and is not due on the clock (the full step covers it).
    private Dictionary<string, long> _due = new(StringComparer.Ordinal);

    // The planners a wake selected, each with the sequence of the mark, so a wave clears only the marks that
    // preceded it.
    private Dictionary<string, ulong> _dirty = new(StringComparer.Ordinal);
    private ulong _seq;

    /// <summary>The sequence number the next mark takes.</summary>
    public ulong Seq => _seq;

    // Set by evidence that can change anything (an authority change, an outcome whose kind the scheduler never
    // armed): the next planner wave runs everything, waits included.
    private bool _all;

    /// <summary>Whether every planner is owed a run (an authority change or a settled window).</summary>
    public bool All
    {
        get => _all;
        set => _all = value;
    }

    // The planners that reported existing work of their kinds still open, keyed by name: none is re-run until
    // one of the attempts it waits on reaches its outcome row, its tick deadline passes, or a full step runs.
    private Dictionary<string, PlannerWait> _waits = new(StringComparer.Ordinal);

    // The planners whose last run was refused admission: they are due again at a tick, which a stopped clock
    // never reaches, so Stalled re-marks them.
    private HashSet<string> _refused = new(StringComparer.Ordinal);

    /// <summary>Filters the catalog to the planners the scheduler runs; null admits every entry. An unconfigured planner is never marked or due.</summary>
    public Func<PlannerEntry, bool>? Configured { get; set; }

    /// <summary>The planner table the queue tracks (the scheduler's); null means the full catalog.</summary>
    public Func<IReadOnlyList<PlannerEntry>>? Catalog { get; set; }

    /// <summary>Copies the queue for a selection that must not persist its marks (the selection previews the step before the bundle read).</summary>
    public PlannerQueue Clone()
    {
        return new PlannerQueue
        {
            _due = new Dictionary<string, long>(_due, StringComparer.Ordinal),
            _dirty = new Dictionary<string, ulong>(_dirty, StringComparer.Ordinal),
            _waits = new Dictionary<string, PlannerWait>(_waits, StringComparer.Ordinal),
            _refused = new HashSet<string>(_refused, StringComparer.Ordinal),
            _seq = _seq,
            _all = _all,
            Configured = Configured,
            Catalog = Catalog,
        };
    }

    /// <summary>Lists the catalog planners the queue tracks.</summary>
    public IReadOnlyList<PlannerEntry> Entries()
    {
        var catalog = Catalog?.Invoke() ?? ClockPlannerCatalog.Entries;
        return Configured is null ? catalog : catalog.Where(Configured).ToList();
    }

    /// <summary>
    /// Folds one wake reason into the queue: the planners of the outcomes' kinds and the consumers of the dirty
    /// sections are marked, an outcome releases the waits naming its attempt, and authority (or an outcome of
    /// unknown kind) marks everything. A wake carrying nothing selectable is a settled window: every planner is
    /// marked. Families without sections on the reason are widened to their sections; a sectionless family
    /// (world) matches by name.
    /// </summary>
    public void Wake(StepReason reason, Func<ActionId, ActionKind?> kindOf)
    {
        if (reason.Authority)
        {
            _all = true;
        }
        var kinds = new HashSet<ActionKind>();
        foreach (var evt in reason.Events)
        {
            if (evt.Terminal)
            {
                Release(evt.Action);
            }
            if (kindOf(evt.Action) is not { } kind)
            {
                _all = true;
                continue;
            }
            kinds.Add(kind);
        }
        var sections = new HashSet<FactSection>(reason.Sections);
        var families = new HashSet<FactFamily>();
        foreach (var family in reason.Families)
        {
            families.Add(family);
            if (reason.Sections.Count == 0)
            {
                foreach (var section in FactStore.FamilySections(family))
                {
                    sections.Add(section);
                }
            }
        }
        if (kinds.Count == 0 && sections.Count == 0 && families.Count == 0 && !reason.Authority)
        {
            MarkAll();
            return;
        }
        foreach (var entry in Entries())
        {
            if (Matches(entry, kinds, sections, families))
            {
                // Protective waits describe the prior safety facts. A relevant observation must reconsider
                // them even while issued work is open; family policy decides whether that work should continue.
                if (ImmediatePlanner(entry))
                {
                    _waits.Remove(entry.Name);
                }
                Mark(entry.Name);
            }
        }
    }

    private static bool Matches(PlannerEntry entry, HashSet<ActionKind> kinds, HashSet<FactSection> sections, HashSet<FactFamily> families) =>
        entry.Kinds.Any(kinds.Contains) || entry.Sections.Any(sections.Contains) || entry.Families.Any(families.Contains);

    /// <summary>The planners held on open work of their kinds.</summary>
    public IReadOnlyDictionary<string, PlannerWait> Waits => _waits;

    /// <summary>Marks a planner dirty so the next selection runs it.</summary>
    public void Mark(string name)
    {
        _seq++;
        _dirty[name] = _seq;
    }

    /// <summary>Marks every catalog planner dirty: the settled-window wake.</summary>
    private void MarkAll()
    {
        foreach (var entry in Entries())
        {
            Mark(entry.Name);
        }
    }

    /// <summary>Drops every wait naming <paramref name="action"/>: its outcome row landed.</summary>
    private void Release(ActionId action)
    {
        foreach (var (name, wait) in _waits.ToList())
        {
            if (wait.On.Contains(action))
            {
                _waits.Remove(name);
            }
        }
    }

    /// <summary>
    /// Decides the planners a step at <paramref name="tick"/> runs. <paramref name="full"/> runs every planner
    /// (a full step, or pending all-evidence) and clears the waits; <paramref name="settled"/> marks every planner
    /// first (a window just ran). Otherwise the dirty and due planners run, less those whose wait
    /// <paramref name="stillOpen"/> confirms (every attempt waited on still open) and whose deadline is ahead of
    /// the tick; null <paramref name="stillOpen"/> confirms every wait. A wait whose attempt is no longer open is
    /// released here.
    /// </summary>
    public PlannerSelectionResult Selection(long tick, bool full, bool settled, Func<ActionId, bool>? stillOpen)
    {
        if (full || _all)
        {
            return new PlannerSelectionResult(true, null, [], _seq);
        }
        if (settled)
        {
            MarkAll();
        }
        foreach (var (name, wait) in _waits.ToList())
        {
            var open = stillOpen is null || wait.On.All(stillOpen);
            if (!open || wait.Deadline <= tick)
            {
                _waits.Remove(name);
            }
        }
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        var waiting = new List<string>();
        foreach (var entry in Entries())
        {
            var dirty = _dirty.ContainsKey(entry.Name);
            var ran = _due.TryGetValue(entry.Name, out var due);
            if (!dirty && !(ran && due <= tick))
            {
                continue;
            }
            if (_waits.ContainsKey(entry.Name))
            {
                waiting.Add(entry.Name);
                continue;
            }
            chosen.Add(entry.Name);
        }
        if (chosen.Count == 0)
        {
            return new PlannerSelectionResult(false, null, waiting, _seq);
        }
        return new PlannerSelectionResult(true, entry => chosen.Contains(entry.Name), waiting, _seq);
    }

    /// <summary>
    /// Records a wave: each planner that returned (<paramref name="names"/>) gets its next review tick and its
    /// marks up to the selection's began sequence cleared; one that reported existing work of its kinds
    /// (<paramref name="reasonOf"/>, the wave's per-planner reason) waits on those attempts
    /// (<paramref name="openWork"/> names them) until their outcome or its deadline; a full wave (pick null) also
    /// clears the all-evidence flag and every wait. A planner that missed the wave keeps its marks.
    /// </summary>
    public void Ran(PlannerSelectionResult sel, IEnumerable<string> names, Func<string, Verdict?> reasonOf, long tick, Func<IReadOnlyList<ActionKind>, IReadOnlyList<ActionId>>? openWork)
    {
        if (sel.Pick is null)
        {
            _all = false;
            _waits = new Dictionary<string, PlannerWait>(StringComparer.Ordinal);
        }
        var entries = Entries().ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        foreach (var name in names)
        {
            var entry = entries[name];
            _due[name] = tick + entry.ReviewEvery().Value;
            if (_dirty.TryGetValue(name, out var mark) && mark <= sel.Began)
            {
                _dirty.Remove(name);
            }
            var reason = reasonOf(name);
            if (reason is { } finished && finished.Is(Cause.SharedAdmission))
            {
                _refused.Add(name);
            }
            else
            {
                _refused.Remove(name);
            }
            if (reason is not { } done || done != BuildingReasonExistingWork || openWork is null)
            {
                _waits.Remove(name);
                continue;
            }
            var on = openWork(entry.Kinds);
            if (on.Count == 0)
            {
                _waits.Remove(name);
                continue;
            }
            _waits[name] = new PlannerWait(on, _due[name]);
        }
    }

    /// <summary>
    /// The step on a stopped clock (the last window refused no_work and the tick did not move): no tick
    /// deadline or cadence can pass, so the waits are dropped and the refused planners marked, each
    /// re-examining its own work instead of parking the colony for good.
    /// </summary>
    public void Stalled()
    {
        _waits = new Dictionary<string, PlannerWait>(StringComparer.Ordinal);
        foreach (var name in _refused)
        {
            Mark(name);
        }
    }

    /// <summary>
    /// The step on a stopped combat window: the stop is the open fight's next decision, so the defense planner
    /// runs even though it waits on its own fight's open drafts, which stay open for the whole fight and would
    /// otherwise park it until the fight is over.
    /// </summary>
    public void CombatStopped()
    {
        _waits.Remove(DefensePlanner);
        Mark(DefensePlanner);
    }

    /// <summary>
    /// Names the open attempts of the given kinds across plans, in a stable order: the dependency a planner
    /// reporting existing work waits on.
    /// </summary>
    public static IReadOnlyList<ActionId> OpenWorkOfKinds(IEnumerable<PlanState> plans, IReadOnlyList<ActionKind> kinds)
    {
        var wanted = new HashSet<ActionKind>(kinds);
        var output = new List<ActionId>();
        foreach (var plan in plans)
        {
            foreach (var progress in plan.Progress)
            {
                if (!wanted.Contains(progress.Action.Kind) || !ProgressOpen(progress))
                {
                    continue;
                }
                output.Add(progress.View.Action);
            }
        }
        output.Sort();
        return output;
    }

    /// <summary><c>StandardWorkOpen</c> for one progress row.</summary>
    private static bool ProgressOpen(Progress progress) => Standard.WorkOpen([progress]);

    /// <summary>Indexes every open attempt across plans, for the waits' stillOpen check.</summary>
    public static Func<ActionId, bool> OpenWorkIndex(IEnumerable<PlanState> plans)
    {
        var open = new HashSet<ActionId>();
        foreach (var plan in plans)
        {
            foreach (var progress in plan.Progress)
            {
                if (ProgressOpen(progress))
                {
                    open.Add(progress.View.Action);
                }
            }
        }
        return open.Contains;
    }
}

internal static class PlannerQueueExtensions
{
    /// <summary>A copy of the queue, or a fresh one when there is none.</summary>
    public static PlannerQueue CloneOrNew(this PlannerQueue? q) => q?.Clone() ?? new PlannerQueue();
}
