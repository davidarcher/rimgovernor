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

    /// <summary>
    /// Why each selected planner runs: <c>dirty</c> (an invalidation or a hold marked it), <c>due</c> (its cadence
    /// passed), <c>dirty+due</c>, <c>full</c> or <c>all</c> (every planner). Each
    /// planner's planner_step row carries its word as <c>selected</c>, and the clock_step row the counts.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Why { get; init; }
}

/// <summary>
/// The scheduler's due queue over the planner catalog: per planner, the game tick its last evaluation stands
/// until (<see cref="PlannerEntry.ReviewEvery"/>, or for a family entry on <see cref="PlannerEntry.RoundsCadence"/>
/// the tick the last committed rounds review stands until, #3556), the wake evidence it has yet to act on, and the
/// dependency it reported waiting on. A step selects the planners that are dirty or due at its tick and skips those
/// still waiting; the full step (the coarse reconciliation for missed invalidations) and an authority change run
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

    /// <summary>Whether every planner is owed a run (an authority change).</summary>
    public bool All
    {
        get => _all;
        set => _all = value;
    }

    // The planners that reported existing work of their kinds still open, keyed by name: none is re-run until
    // one of the attempts it waits on reaches its outcome row, its tick deadline passes, or a full step runs.
    private Dictionary<string, PlannerWait> _waits = new(StringComparer.Ordinal);

    /// <summary>Filters the catalog to the planners the scheduler runs; null admits every entry. An unconfigured planner is never marked or due.</summary>
    public Func<PlannerEntry, bool>? Configured { get; set; }

    /// <summary>The planner table the queue tracks (the scheduler's); null means the full catalog.</summary>
    public Func<IReadOnlyList<PlannerEntry>>? Catalog { get; set; }

    /// <summary>Lists the catalog planners the queue tracks.</summary>
    public IReadOnlyList<PlannerEntry> Entries()
    {
        var catalog = Catalog?.Invoke() ?? ClockPlannerCatalog.Entries;
        return Configured is null ? catalog : catalog.Where(Configured).ToList();
    }

    /// <summary>
    /// Folds one wake reason into the queue: the consumers of the dirty sections are marked and authority
    /// marks everything. A hold other than the review tick carrying nothing selectable (a hazard, a game pause,
    /// the brain's own hold) is an early review of everything: every planner is marked. The review tick itself
    /// (<see cref="StepCause.Review"/>) marks nothing beyond its evidence, so a quiet review runs only the planners
    /// due at the tick (#3556). Families without sections on the reason are widened to their sections; a
    /// sectionless family (world) matches by name.
    /// </summary>
    public void Wake(StepReason reason)
    {
        if (reason.Authority)
        {
            _all = true;
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
        if (reason.Planners.Count > 0)
        {
            // A planner the wake names is marked; one this host did not compose is not in Entries and is ignored.
            foreach (var entry in Entries().Where(e => reason.Planners.Contains(e.Name)))
            {
                Mark(entry.Name);
            }
        }
        if (sections.Count == 0 && families.Count == 0 && !reason.Authority)
        {
            if (reason.Planners.Count == 0 && reason.Cause != StepCause.Review)
            {
                MarkAll();
            }
            return;
        }
        foreach (var entry in Entries())
        {
            if (Matches(entry, sections, families))
            {
                // Protective waits describe the prior safety facts. A relevant observation must reconsider
                // them even while issued work is open; family policy decides whether that work should continue.
                if (ClockScheduler.ImmediatePlanner(entry))
                {
                    _waits.Remove(entry.Name);
                }
                Mark(entry.Name);
            }
        }
    }

    private static bool Matches(PlannerEntry entry, HashSet<FactSection> sections, HashSet<FactFamily> families) =>
        entry.Sections.Any(sections.Contains) || entry.Families.Any(families.Contains);

    /// <summary>The planners held on open work of their kinds.</summary>
    public IReadOnlyDictionary<string, PlannerWait> Waits => _waits;

    /// <summary>Marks a planner dirty so the next selection runs it.</summary>
    public void Mark(string name)
    {
        _seq++;
        _dirty[name] = _seq;
    }

    /// <summary>Marks every catalog planner dirty: the wake of a hold that is an early review of everything.</summary>
    private void MarkAll()
    {
        foreach (var entry in Entries())
        {
            Mark(entry.Name);
        }
    }

    /// <summary>
    /// Decides the planners a step at <paramref name="tick"/> runs. <paramref name="full"/> runs every planner
    /// (a full step, or pending all-evidence) and clears the waits. Otherwise the dirty and due planners run,
    /// less those whose wait <paramref name="stillOpen"/> confirms (every attempt waited on still open) and whose
    /// deadline is ahead of the tick; null <paramref name="stillOpen"/> confirms every wait. A wait whose attempt
    /// is no longer open is released here.
    /// </summary>
    public PlannerSelectionResult Selection(long tick, bool full, Func<ActionId, bool>? stillOpen)
    {
        if (full || _all)
        {
            var everything = full ? "full" : "all";
            return new PlannerSelectionResult(true, null, [], _seq) { Why = Entries().ToDictionary(e => e.Name, _ => everything, StringComparer.Ordinal) };
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
        var why = new Dictionary<string, string>(StringComparer.Ordinal);
        var waiting = new List<string>();
        foreach (var entry in Entries())
        {
            var dirty = _dirty.ContainsKey(entry.Name);
            var due = _due.TryGetValue(entry.Name, out var dueTick) && dueTick <= tick;
            if (!dirty && !due)
            {
                continue;
            }
            if (_waits.ContainsKey(entry.Name))
            {
                waiting.Add(entry.Name);
                continue;
            }
            chosen.Add(entry.Name);
            why[entry.Name] = dirty && due ? "dirty+due" : !dirty ? "due" : "dirty";
        }
        if (chosen.Count == 0)
        {
            return new PlannerSelectionResult(false, null, waiting, _seq);
        }
        return new PlannerSelectionResult(true, entry => chosen.Contains(entry.Name), waiting, _seq) { Why = why };
    }

    /// <summary>
    /// Records a wave: each planner that returned (<paramref name="names"/>) gets its next review tick and its
    /// marks up to the selection's began sequence cleared, unless it reported a wait only wall-clock time ends
    /// (<see cref="Verdict.WaitsOnRealTime"/>: a force-pausing dialog's grace, which no tick or event ends, #3697),
    /// which stays marked so the next step runs it again; one that reported existing work of its kinds
    /// (<paramref name="reasonOf"/>, the wave's per-planner reason) waits on those attempts
    /// (<paramref name="openWork"/> names them) until their outcome or its deadline; a full wave (pick null) also
    /// clears the all-evidence flag and every wait. A planner that missed the wave keeps its marks. An entry on the
    /// rounds cadence is due at <paramref name="roundsDue"/>, the tick the last committed rounds review stands until
    /// (null before the first review): every such entry that has run, whether or not this wave ran it, so a review
    /// the rounder committed on an early step moves them all to its next due tick and none runs alone on the last
    /// review between two (#3556).
    /// </summary>
    public void Ran(PlannerSelectionResult sel, IEnumerable<string> names, Func<string, Verdict?> reasonOf, long tick, Func<IReadOnlyList<ActionKind>, IReadOnlyList<ActionId>>? openWork, long? roundsDue = null)
    {
        if (sel.Pick is null)
        {
            _all = false;
            _waits = new Dictionary<string, PlannerWait>(StringComparer.Ordinal);
        }
        var entries = Entries().ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var returned = names.ToList();
        if (sel.Pick is null)
        {
            // A full wave selected every planner: one that missed the cutoff has no cadence yet and no mark, so it
            // is due at once rather than waiting for the next full step (#2827).
            foreach (var name in entries.Keys.Except(returned, StringComparer.Ordinal))
            {
                _due.TryAdd(name, tick);
            }
        }
        if (roundsDue is { } aligned)
        {
            foreach (var (name, entry) in entries)
            {
                if (entry.RoundsCadence && _due.ContainsKey(name))
                {
                    _due[name] = aligned;
                }
            }
        }
        foreach (var name in returned)
        {
            var entry = entries[name];
            _due[name] = entry.RoundsCadence && roundsDue is { } due ? due : tick + entry.ReviewEvery().Value;
            var reason = reasonOf(name);
            if (_dirty.TryGetValue(name, out var mark) && mark <= sel.Began && reason?.WaitsOnRealTime != true)
            {
                _dirty.Remove(name);
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
    /// The step on a stopped combat run: the stop is the open fight's next decision, so the defense planner
    /// runs even though it waits on its own fight's open drafts, which stay open for the whole fight and would
    /// otherwise park it until the fight is over.
    /// </summary>
    public void CombatStopped()
    {
        _waits.Remove(ClockScheduler.DefensePlanner);
        Mark(ClockScheduler.DefensePlanner);
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
    private static bool ProgressOpen(Progress progress) => WorkProgress.Open([progress]);

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
