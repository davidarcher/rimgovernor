
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>A goal's winning note with the outcome of the verdict that filed it (admitted and nothing_to_do both file the zero note).</summary>
internal readonly record struct PlannerFiling(PlannerNote Note, Outcome Outcome);

/// <summary>The note a goal stands on and the game tick it began standing on that cause.</summary>
internal readonly record struct PlannerStanding(PlannerNote Note, long Since);

/// <summary>
/// One change of a goal's winning cause: the new cause with its subject and outcome, and the cause it replaced.
/// <see cref="HadPrevious"/> is false for the first note seen since the process started (the past is unknown);
/// <see cref="Held"/> is null when the ticks the previous cause stood are unknown (the game clock went backwards,
/// as a save load does).
/// </summary>
internal sealed record ConcernTransitionEvent(ConcernId Concern, PlannerFiling Filing, bool HadPrevious = false, Cause? PreviousCause = null, long? Held = null)
{
    /// <summary>The cause the concern held before this change; none when it had no previous standing.</summary>
    public Cause Previous => PreviousCause ?? Cause.None;
}

/// <summary>
/// Remembers the standing note per goal; the idle-stall row lists them as the standing refusals and
/// <see cref="Changed"/> turns a change of cause into a transition. It lives in memory only: after a restart the
/// first note of a goal has no known past. Only the step task touches it.
/// </summary>
internal sealed class PlannerReasonLog
{
    private readonly Dictionary<ConcernId, PlannerStanding> _last = [];

    /// <summary>
    /// Files the wave's notes at <paramref name="tick"/> and returns the goals whose cause changed, in goal
    /// order. A subject change under the same cause files silently, and a goal first seen clear is not a
    /// transition (nothing to explain).
    /// </summary>
    public IReadOnlyList<ConcernTransitionEvent> Changed(IReadOnlyDictionary<ConcernId, PlannerFiling> filings, long tick)
    {
        var output = new List<ConcernTransitionEvent>();
        foreach (var goal in filings.Keys.Order())
        {
            var filing = filings[goal];
            var seen = _last.TryGetValue(goal, out var prior);
            if (seen && prior.Note.Cause == filing.Note.Cause)
            {
                _last[goal] = new PlannerStanding(filing.Note, prior.Since);
                continue;
            }
            _last[goal] = new PlannerStanding(filing.Note, tick);
            if (!seen)
            {
                if (filing.Note.Cause != Cause.None)
                {
                    output.Add(new ConcernTransitionEvent(goal, filing));
                }
                continue;
            }
            var held = tick - prior.Since;
            output.Add(new ConcernTransitionEvent(goal, filing, true, prior.Note.Cause, held >= 0 ? held : null));
        }
        return output;
    }
}

internal static class PlannerReasons
{
    /// <summary>
    /// What a planner's verdict files on its goal's record: the verdict's cause and its bounded subject. A
    /// refusal and a wait file their cause; a planner whose fight is running orders (combat_orders) or fell back
    /// to squad defense (hold_fallback) files its own wait cause, since the fight is under way and nothing
    /// failed; a planner that admitted or saw no deficit files the zero note, which clears the goal's filed
    /// refusal or wait; a switched-off planner files the opt-out hold. Null skips filing: no verdict, an invalid
    /// one, or an outcome that says nothing about the goal (no review to judge, a stale proposal).
    /// </summary>
    public static PlannerNote? RecordReason(Verdict v)
    {
        if (v.IsZero)
        {
            return null;
        }
        try
        {
            v.Validate();
        }
        catch (DomainException)
        {
            return null;
        }
        switch (v.Outcome)
        {
            case Outcome.Admitted or Outcome.NothingToDo:
                return new PlannerNote();
            case Outcome.Disabled:
                return new PlannerNote { Cause = Cause.HeldOptIn };
            case Outcome.NoReview or Outcome.Expired:
                return null;
            case Outcome.OrdersSent:
                return new PlannerNote { Cause = Cause.CombatOrders };
            case Outcome.HoldFallback:
                return new PlannerNote { Cause = Cause.HoldFallback };
            default:
                return new PlannerNote { Cause = v.Refusal.Kind, Subject = BoundSubject(v.Refusal.Subject) };
        }
    }

    /// <summary>
    /// Orders the notes sibling planners file on one goal: a refusal outranks a wait, a wait outranks a clear (an
    /// enabled sibling that is idle), and a clear outranks the opt-out, which stands only when every planner of
    /// the goal is switched off.
    /// </summary>
    public static int NoteRank(PlannerNote n)
    {
        if (n.Cause == Cause.None)
        {
            return 1;
        }
        if (n.Cause == Cause.HeldOptIn)
        {
            return 0;
        }
        return n.Cause.Waiting() ? 2 : 3;
    }

    /// <summary>Collects the goal filings of the planners that returned; siblings on one goal keep the highest-ranked note.</summary>
    public static Dictionary<ConcernId, PlannerFiling> WavePlannerReasons(IEnumerable<string> names, Func<string, (ConcernId Goal, Verdict Verdict)?> filing)
    {
        var output = new Dictionary<ConcernId, PlannerFiling>();
        foreach (var name in names)
        {
            if (filing(name) is not { } found)
            {
                continue;
            }
            FileReason(output, found.Goal, found.Verdict);
        }
        return output;
    }

    /// <summary>Files <paramref name="verdict"/> on <paramref name="goal"/> in <paramref name="output"/>, keeping the highest-ranked note.</summary>
    public static void FileReason(Dictionary<ConcernId, PlannerFiling> output, ConcernId goal, Verdict verdict)
    {
        if (RecordReason(verdict) is not { } note)
        {
            return;
        }
        if (!output.TryGetValue(goal, out var prior) || NoteRank(note) > NoteRank(prior.Note))
        {
            output[goal] = new PlannerFiling(note, verdict.Outcome);
        }
    }

    /// <summary>
    /// Turns transitions into concern_transitions rows: concern the goal, verdict the filing verdict's outcome,
    /// cause the new cause (empty when it cleared), subject, method (the goal's current method on its progress
    /// record), and previous cause and held ticks only when known.
    /// </summary>
    public static List<ConcernTransition> TransitionRows(IReadOnlyList<ConcernTransitionEvent> transitions, IReadOnlyDictionary<ConcernId, string> methods, long tick, DateTimeOffset now)
    {
        var rows = new List<ConcernTransition>(transitions.Count);
        foreach (var t in transitions)
        {
            rows.Add(new ConcernTransition
            {
                WallMs = now.ToUnixTimeMilliseconds(),
                Tick = tick,
                Concern = t.Concern.Value,
                Verdict = Wire.Name(t.Filing.Outcome),
                Cause = Wire.Name(t.Filing.Note.Cause),
                Subject = t.Filing.Note.Subject,
                Method = methods.GetValueOrDefault(t.Concern, ""),
                PreviousKnown = t.HadPrevious,
                Previous = Wire.Name(t.Previous),
                Held = t.Held,
            });
        }
        return rows;
    }

    /// <summary>
    /// Files the step's own failure to record a wave (the goal notes or the open work of the planners' kinds) as
    /// a failed planner_step row, so a store error is not swallowed.
    /// </summary>
    public static void PlannerBookkeepingFailed(string target, Exception error) =>
        Tel.Decide(new Decision
        {
            Kind = "planner_step",
            Component = "clock-scheduler",
            Level = TelemetryLevel.Warn,
            Verdict = "failed",
            Reason = "journal_error",
            Target = target,
            Attrs = new Dictionary<string, object?> { ["error"] = error },
        });
}

public sealed partial class ClockScheduler
{
    /// <summary>
    /// Notes each goal's planner refusal or wait and files it on the goal's progress record, so the status strip
    /// says why a goal has no method or what it waits on (the planner_step rows carry the same reasons). A goal
    /// whose cause changed also files a concern_transitions row.
    /// </summary>
    internal async Task RecordPlannerReasonsAsync(PlannerWave wave, long tick, CancellationToken ct)
    {
        var filings = PlannerReasons.WavePlannerReasons(wave.FinishedNames(), wave.Filing);
        // The ledger files its bill purposes' verdicts beside its own.
        foreach (var report in wave.Reports())
        {
            PlannerReasons.FileReason(filings, report.Key, report.Verdict);
        }
        if (filings.Count == 0)
        {
            return;
        }
        var transitions = _plannerReasons.Changed(filings, tick);
        if (_player?.Journal is not { } journal)
        {
            return;
        }
        var notes = filings.ToDictionary(kv => kv.Key, kv => kv.Value.Note);
        IReadOnlyDictionary<ConcernId, string> methods = new Dictionary<ConcernId, string>();
        try
        {
            methods = (await journal.RecordPlannerReasonsAsync(notes, ct).ConfigureAwait(false)).Methods;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            PlannerReasons.PlannerBookkeepingFailed("reasons", e);
        }
        try
        {
            await journal.RecordConcernTransitionsAsync(PlannerReasons.TransitionRows(transitions, methods, tick, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            PlannerReasons.PlannerBookkeepingFailed("concern_transitions", e);
        }
    }
}
