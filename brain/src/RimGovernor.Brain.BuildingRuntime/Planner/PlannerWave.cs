
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Bounds one clock step's planner waves, each part reported on the step's clock_step row beside what the step
/// used.
/// </summary>
public sealed record StepBudget
{
    /// <summary>The planner waves' wall budget, counted from the wave's start (after the rounds): under the 60 s step call, so a wave that never returns holds admission with its planners named instead of failing the step on the context deadline.</summary>
    public static readonly TimeSpan DefaultStepWall = TimeSpan.FromSeconds(40);

    /// <summary>The most native round trips a step may spend; zero leaves it unbounded. It is reported, and an overrun is flagged on the row, never enforced mid-read.</summary>
    public ulong Reads { get; init; }

    /// <summary>Bounds the planner waves: the critical wave still pending at the deadline holds admission naming its planners, and the optional wave's cutoff is the same deadline. Zero or less means <see cref="DefaultStepWall"/>.</summary>
    public TimeSpan Wall { get; init; }

    internal TimeSpan EffectiveWall => Wall <= TimeSpan.Zero ? DefaultStepWall : Wall;
}

/// <summary>
/// One step's planner wave: the group that runs the planners, the optional planners' cancellable token, each
/// planner's private result and which of them returned before the cutoff. Planners write into a private
/// <see cref="ClockSchedulerResult"/>, so an optional planner still running after the step has moved on writes
/// nothing the step reads; <see cref="Merge"/> copies the results of the planners that made the cutoff.
/// </summary>
internal sealed class PlannerWave : IDisposable
{
    private readonly object _mu = new();
    private readonly CancellationTokenSource _optional;
    private readonly Dictionary<string, ClockSchedulerResult> _results = new(StringComparer.Ordinal);
    private readonly List<string> _finished = [];
    // Each returned planner's reason (its run's, or for a proposal planner its proposal's outcome): what the due
    // queue reads to tell a planner waiting on open work from one that is due.
    private readonly Dictionary<string, Verdict> _reasons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConcernId> _concerns = new(StringComparer.Ordinal);
    // The class each planner was queued under: the entry's own or a startup promotion of it.
    private readonly Dictionary<string, bool> _critical = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _began = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _took = new(StringComparer.Ordinal);
    // The family entries (#3250): they file under their NeedIds, never their own name.
    private readonly HashSet<string> _families = new(StringComparer.Ordinal);
    private bool _closed;

    public PlannerWave(CancellationToken call)
    {
        _optional = CancellationTokenSource.CreateLinkedTokenSource(call);
        Group = new PlannerGroup(PlannerGroup.PlannerWidth, call);
        Call = call;
    }

    public PlannerGroup Group { get; }

    private CancellationToken Call { get; }

    /// <summary>Queues <paramref name="entry"/>'s run on the wave: a critical planner under the step token, an optional one under the wave's cancellable token. <paramref name="selected"/> is why the selection chose it (<see cref="PlannerSelectionResult.Why"/>), carried on its planner_step rows; <paramref name="cadence"/> says it runs for its cadence alone (<see cref="FamilyStep.Cadence"/>, #3556).</summary>
    public void Queue(ClockScheduler s, StepArbiter arbiter, PlannerEntry entry, CancellationToken epoch, string? selected = null, bool cadence = false)
    {
        var priv = new ClockSchedulerResult();
        lock (_mu)
        {
            _results[entry.Name] = priv;
            _critical[entry.Name] = entry.Class == PlannerClass.Critical;
            _concerns[entry.Name] = entry.FilingKey();
            _began[entry.Name] = DateTimeOffset.UtcNow;
            if (entry.Family is not null)
            {
                _families.Add(entry.Name);
            }
        }
        var ct = entry.Class == PlannerClass.Critical ? Call : _optional.Token;
        var start = DateTimeOffset.UtcNow;
        Group.Go(entry.Name, entry.Class, async () =>
        {
            using var audit = entry.AuditReads();
            using var reads = ReadTallyScope.Nested();
            using var span = Tel.StartSpan("buildingruntime", "planner." + entry.Name, new Dictionary<string, object?>
            {
                ["rimgovernor.class"] = entry.Class.ToString().ToLowerInvariant(),
                ["rimgovernor.concern"] = entry.Concern.Value,
            });
            using var runtime = Tel.RuntimeTags(span);
            var reason = default(Verdict);
            Exception? error = null;
            try
            {
                await s.Config.Faults.PlannerFault(entry.Name, async () =>
                {
                    reason = await entry.Run(s, ct, epoch, priv, arbiter, cadence).ConfigureAwait(false);
                }, ct).Invoke().ConfigureAwait(false);
            }
            catch (CombatWorkOpenException)
            {
                reason = BuildingReasonExistingWork;
            }
            catch (Exception e)
            {
                error = e;
            }
            var took = DateTimeOffset.UtcNow - start;
            bool late;
            lock (_mu)
            {
                _took[entry.Name] = took;
                late = _closed;
            }
            var decision = Scoped(Tallied(StepDecision(entry, reason, error, took, late), reads.Tally));
            // A family's run files one planner_step row per NeedId, keyed by the NeedId as its concern, so explain
            // pairs each with the need_transitions row filed under the same key; the family's own row stands
            // only for a failure or a run that judged no Need.
            var needRows = error is null && priv.Families.TryGetValue(entry.Name, out var family) && family.Reports.Count > 0
                ? family.Reports.Select(r => Scoped(Tallied(NeedDecision(entry, r, took, late, family.Kept.Contains(r.Need)), reads.Tally))).ToList()
                : null;
            foreach (var row in needRows ?? [decision])
            {
                Tel.Decide(selected is null ? row : row with { Attrs = new Dictionary<string, object?>(row.Attrs) { ["selected"] = selected } });
            }
            span?.SetTag("rimgovernor.verdict", decision.Verdict);
            span?.SetTag("rimgovernor.reason", decision.Reason);
            span?.SetTag("rimgovernor.late", late);
            Tel.TagReads(span, reads.Tally);
            if (decision.Verdict == "failed")
            {
                Tel.EndSpan(span, error);
            }
            if (error is not null)
            {
                error = new PlannerException(entry.Name, error);
            }
            if (error is null)
            {
                s.Config.Rounds?.NoteResearchWait(reason);
            }
            var failure = Done(entry.Name, reason, error);
            if (failure is not null)
            {
                throw failure;
            }
        });
    }

    /// <summary>
    /// The planner_step row of one planner run: the planner (target), its verdict word and cause, wall time,
    /// concern, class and, for a failure, the error. A run that returned after the cutoff is marked late. A
    /// refused or waiting verdict's reason is its cause (the subject rides in attrs); any other outcome beyond
    /// admitted reads as ok with the outcome as the reason; a run with no verdict reads ok/no_verdict.
    /// </summary>
    internal static Decision StepDecision(PlannerEntry entry, Verdict v, Exception? err, TimeSpan took, bool late)
    {
        if (late && entry.Class == PlannerClass.Optional && err is OperationCanceledException)
        {
            v = Verdict.AwaitingPlan("planner", "cutoff");
            err = null;
        }
        var attrs = new Dictionary<string, object?>
        {
            ["concern"] = entry.Concern.Value,
            ["class"] = entry.Class.ToString().ToLowerInvariant(),
        };
        var d = new Decision
        {
            Kind = Decision.PlannerStepKind,
            Component = "clock-scheduler",
            Target = entry.Name,
            Dur = took,
        };
        if (err is not null)
        {
            d = d with { Level = TelemetryLevel.Warn };
            d = d with { Verdict = "failed" };
            d = d with { Reason = "error" };
            attrs["error"] = err;
        }
        else if (v.IsZero)
        {
            d = d with { Verdict = "ok" };
            d = d with { Reason = "no_verdict" };
        }
        else if (v.Outcome is Outcome.Admitted or Outcome.Waiting or Outcome.Refused)
        {
            d = d with { Verdict = Wire.Name(v.Outcome) };
            d = d with { Reason = v.Outcome == Outcome.Admitted ? "plan_admitted" : v.Refusal.Kind.Value };
        }
        else
        {
            d = d with { Verdict = "ok" };
            d = d with { Reason = Wire.Name(v.Outcome) };
        }
        if (v.Refusal.Subject != "")
        {
            attrs["subject"] = v.Refusal.Subject;
        }
        if (late)
        {
            attrs["late"] = true;
        }
        return d with { Attrs = attrs };
    }

    /// <summary>A planner's rows carry the reads its whole run issued, and their game-thread wait and run time.</summary>
    private static Decision Tallied(Decision decision, ReadTally tally)
    {
        var attrs = new Dictionary<string, object?>(decision.Attrs);
        foreach (var (key, value) in tally.Attrs())
        {
            attrs[key] = value;
        }
        return decision with { Attrs = attrs };
    }

    /// <summary>The immediate review's rows carry its scope and decision tick.</summary>
    private static Decision Scoped(Decision decision)
    {
        if (!ReviewScope.Immediate)
        {
            return decision;
        }
        var attrs = new Dictionary<string, object?>(decision.Attrs) { ["scope"] = "immediate" };
        if (ReadValidityScope.TryGet(out var validity))
        {
            attrs["decision_tick"] = (long)validity.Tick.Value;
        }
        return decision with { Attrs = attrs };
    }

    /// <summary>The planner_step row of one Need a family run judged: the family entry's row with the NeedId as its concern, marked <c>kept</c> when the run kept the verdict from its last run instead of working the Need (#3556).</summary>
    internal static Decision NeedDecision(PlannerEntry entry, NeedReport report, TimeSpan took, bool late, bool kept = false)
    {
        var d = StepDecision(entry, report.Verdict, null, took, late);
        var attrs = new Dictionary<string, object?>(d.Attrs) { ["concern"] = report.Need.Value };
        if (kept)
        {
            attrs["kept"] = true;
        }
        return d with { Attrs = attrs };
    }

    /// <summary>
    /// Records a planner's return and its reason. After the cutoff the return is discarded: the planner was
    /// recorded missed, its result is not merged and its (cancellation) error is not a failure.
    /// </summary>
    private Exception? Done(string name, Verdict reason, Exception? error)
    {
        lock (_mu)
        {
            if (_closed)
            {
                return null;
            }
            _finished.Add(name);
            if (error is null)
            {
                _reasons[name] = reason;
            }
            return error;
        }
    }

    /// <summary>The reason recorded for <paramref name="name"/>; null for a planner that failed or missed the cutoff.</summary>
    public Verdict? Reason(string name)
    {
        lock (_mu)
        {
            return _reasons.TryGetValue(name, out var reason) ? reason : null;
        }
    }

    /// <summary>The key and verdict a returned planner files on (its filing key); null for a planner that failed or missed the cutoff.</summary>
    public (ConcernId Goal, Verdict Verdict)? Filing(string name)
    {
        lock (_mu)
        {
            return !_families.Contains(name) && _reasons.TryGetValue(name, out var verdict) ? (_concerns[name], verdict) : null;
        }
    }

    /// <summary>Each returned family's verdicts under its NeedIds, and those it files beside them (the work ledger's purposes).</summary>
    public IReadOnlyList<LedgerReport> Reports()
    {
        lock (_mu)
        {
            var output = new List<LedgerReport>();
            foreach (var name in _finished)
            {
                if (_reasons.ContainsKey(name) && _results[name].Families.TryGetValue(name, out var family))
                {
                    output.AddRange(family.Reports.Select(r => new LedgerReport(r.Need.FilingKey, r.Verdict)));
                    output.AddRange(family.Filed);
                }
            }
            return output;
        }
    }

    /// <summary>Each returned family run's Need progress (#3231 decision 18), in return order; see <see cref="NeedProgressOf"/>.</summary>
    public IReadOnlyList<FamilyProgress> NeedProgress()
    {
        lock (_mu)
        {
            var output = new List<FamilyProgress>();
            foreach (var name in _finished)
            {
                if (_reasons.ContainsKey(name) && _results[name].Families.TryGetValue(name, out var run) && NeedProgressOf(name, run) is { } progress)
                {
                    output.Add(progress);
                }
            }
            return output;
        }
    }

    /// <summary>
    /// What one family run files on <c>Rounds.Progress</c>: every reported Need with its filed note and the Method it
    /// works, met when its standing is. Null for a run that judged nothing (no review, switched off), so its rows stand.
    /// </summary>
    internal static FamilyProgress? NeedProgressOf(string family, FamilyRun run)
    {
        if (run.Standings.Count == 0 && !run.Complete)
        {
            return null;
        }
        var met = run.Standings.Where(s => s.Readiness == NeedReadiness.Met).Select(s => s.Need.Id).ToHashSet();
        var needs = new List<NeedProgress>(run.Reports.Count);
        foreach (var report in run.Reports)
        {
            var note = PlannerReasons.RecordReason(report.Verdict) ?? new PlannerNote();
            needs.Add(new NeedProgress(report.Need, met.Contains(report.Need) && report.Method.IsEmpty, note, report.Method));
        }
        return new FamilyProgress(family, run.Complete, needs);
    }

    /// <summary>Whether the named planner was queued into this wave's critical cycle, promotion included: a pending one holds admission instead of being recorded as having missed the cutoff.</summary>
    public bool QueuedCritical(string name)
    {
        lock (_mu)
        {
            return _critical.GetValueOrDefault(name);
        }
    }

    /// <summary>The planners that returned before the cutoff, in return order.</summary>
    public IReadOnlyList<string> FinishedNames()
    {
        lock (_mu)
        {
            return [.. _finished];
        }
    }

    /// <summary>
    /// Ends the wave at the cutoff: the optional planners still running are cancelled and their later returns
    /// discarded. Returns the names still pending, in queue order.
    /// </summary>
    public IReadOnlyList<string> Close()
    {
        IReadOnlyList<string> pending;
        lock (_mu)
        {
            pending = Group.Pending();
            _closed = true;
        }
        _optional.Cancel();
        return pending;
    }

    /// <summary>Each queued planner's wall time in milliseconds since it was queued: its full run once it returned, the elapsed time so far for one still evaluating (a missed cutoff).</summary>
    public SortedDictionary<string, double> PlannerMs()
    {
        lock (_mu)
        {
            var output = new SortedDictionary<string, double>(StringComparer.Ordinal);
            foreach (var (name, start) in _began)
            {
                var d = _took.TryGetValue(name, out var took) ? took : DateTimeOffset.UtcNow - start;
                output[name] = d.TotalMilliseconds;
            }
            return output;
        }
    }

    /// <summary>Copies the results of every planner that returned before the cutoff onto <paramref name="output"/>: each planner's private result holds its own member alone.</summary>
    public void Merge(ClockSchedulerResult output)
    {
        lock (_mu)
        {
            foreach (var name in _finished)
            {
                output.MergeFrom(_results[name]);
            }
        }
    }

    /// <summary>A task completed once <paramref name="d"/> has passed (at once for d of zero or less).</summary>
    public static Task After(TimeSpan d) => d <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(d);

    public void Dispose() => _optional.Dispose();
}

/// <summary>A planner's error wrapped with the planner's name, so a recorded failure names its source.</summary>
public sealed class PlannerException(string planner, Exception inner) : Exception($"{planner}: {inner.Message}", inner)
{
    public string Planner { get; } = planner;
}
