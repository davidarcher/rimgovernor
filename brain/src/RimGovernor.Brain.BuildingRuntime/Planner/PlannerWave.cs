
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Bounds one clock step's planner waves, each part reported on the step's clock_step row beside what the step
/// used.
/// </summary>
public sealed record StepBudget
{
    /// <summary>The planner waves' wall budget, counted from the wave's start (after the rounds): under the 60 s step call, so a wave that never returns holds admission with its planners named instead of failing the step on the context deadline.</summary>
    public static readonly TimeSpan DefaultStepWall = TimeSpan.FromSeconds(40);

    /// <summary>The least grace the optional wave gets after the critical wave returns. A step whose critical wave is quicker than this still lets the optional reviews finish.</summary>
    public static readonly TimeSpan DefaultOptionalGrace = TimeSpan.FromSeconds(1);

    /// <summary>The most native round trips a step may spend; zero leaves it unbounded. It is reported, and an overrun is flagged on the row, never enforced mid-read.</summary>
    public ulong Reads { get; init; }

    /// <summary>Bounds the planner waves: the critical wave still pending at the deadline holds admission naming its planners, and the optional wave's cutoff never passes it. Zero or less means <see cref="DefaultStepWall"/>.</summary>
    public TimeSpan Wall { get; init; }

    /// <summary>Bounds the native-work window a step lends (the largest NativeWorkTicks its planners ask for); zero means the window's own maximum.</summary>
    public uint NativeWork { get; init; }

    /// <summary>Floors how long after the critical wave returns the step still joins the optional wave; zero or less means <see cref="DefaultOptionalGrace"/>.</summary>
    public TimeSpan OptionalGrace { get; init; }

    internal TimeSpan EffectiveWall => Wall <= TimeSpan.Zero ? DefaultStepWall : Wall;

    internal TimeSpan EffectiveOptionalGrace => OptionalGrace <= TimeSpan.Zero ? DefaultOptionalGrace : OptionalGrace;
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
    private bool _closed;

    public PlannerWave(CancellationToken call)
    {
        _optional = CancellationTokenSource.CreateLinkedTokenSource(call);
        Group = new PlannerGroup(call, PlannerGroup.PlannerWidth);
        Call = call;
    }

    public PlannerGroup Group { get; }

    private CancellationToken Call { get; }

    /// <summary>Queues <paramref name="entry"/>'s run on the wave: a critical planner under the step token, an optional one under the wave's cancellable token.</summary>
    public void Queue(ClockScheduler s, CancellationToken epoch, StepArbiter arbiter, PlannerEntry entry)
    {
        var priv = new ClockSchedulerResult();
        lock (_mu)
        {
            _results[entry.Name] = priv;
            _critical[entry.Name] = entry.Class == PlannerClass.Critical;
            _concerns[entry.Name] = entry.FilingKey();
            _began[entry.Name] = DateTimeOffset.UtcNow;
        }
        var ct = entry.Class == PlannerClass.Critical ? Call : _optional.Token;
        var start = DateTimeOffset.UtcNow;
        Group.Go(entry.Name, entry.Class, entry.Priority, async () =>
        {
            using var audit = entry.AuditReads();
            using var span = Tel.StartSpan("buildingruntime", "planner." + entry.Name, new Dictionary<string, object?>
            {
                ["rimgovernor.class"] = entry.Class.ToString().ToLowerInvariant(),
                ["rimgovernor.concern"] = entry.Concern.Value,
            });
            var reason = default(Verdict);
            Exception? error = null;
            try
            {
                await s.Config.Faults.PlannerFault(entry.Name, ct, async () =>
                {
                    reason = await entry.Run(s, ct, epoch, priv, arbiter).ConfigureAwait(false);
                }).Invoke().ConfigureAwait(false);
            }
            catch (StaleOwnerException)
            {
                // Planners share a standard (foodAcquisition and fields), so a sibling's admission in the same
                // wave moves the owner under a slow planner; it waits for the next round, not a failure.
                reason = Verdict.AwaitingPlan("owner", "revision_changed");
            }
            catch (OpenMethodException)
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
            var decision = StepDecision(entry, reason, error, took, late);
            if (ReviewScope.Immediate)
            {
                decision.Attrs["scope"] = "immediate";
                if (ReadValidityScope.TryGet(out var validity))
                {
                    decision.Attrs["decision_tick"] = (long)validity.Tick.Value;
                }
            }
            Tel.Decide(decision);
            span.SetVerdict(decision.Verdict, decision.Reason, late);
            if (decision.Verdict == "failed")
            {
                span.Fail(error);
            }
            if (error is not null)
            {
                error = new PlannerException(entry.Name, error);
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
        var d = new Decision
        {
            Kind = "planner_step",
            Component = "clock-scheduler",
            Target = entry.Name,
            Dur = took,
            Attrs = new Dictionary<string, object?>
            {
                ["concern"] = entry.Concern.Value,
                ["class"] = entry.Class.ToString().ToLowerInvariant(),
            },
        };
        if (err is not null)
        {
            d.Level = TelemetryLevel.Warn;
            d.Verdict = "failed";
            d.Reason = "error";
            d.Attrs["error"] = err;
        }
        else if (v.IsZero)
        {
            d.Verdict = "ok";
            d.Reason = "no_verdict";
        }
        else if (v.Outcome is Outcome.Admitted or Outcome.Waiting or Outcome.Refused)
        {
            d.Verdict = Wire.Name(v.Outcome);
            d.Reason = v.Outcome == Outcome.Admitted ? "plan_admitted" : Wire.Name(v.Refusal.Kind);
        }
        else
        {
            d.Verdict = "ok";
            d.Reason = Wire.Name(v.Outcome);
        }
        if (v.Refusal.Subject != "")
        {
            d.Attrs["subject"] = v.Refusal.Subject;
        }
        if (late)
        {
            d.Attrs["late"] = true;
        }
        return d;
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

    /// <summary>Records a reason decided after the planner returned: a proposal planner's proposal outcome from the coordinator.</summary>
    public void Decided(string name, Verdict reason)
    {
        lock (_mu)
        {
            _reasons[name] = reason;
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
            return _reasons.TryGetValue(name, out var verdict) ? (_concerns[name], verdict) : null;
        }
    }

    /// <summary>The purpose verdicts the returned ledger step filed beside its own.</summary>
    public IReadOnlyList<LedgerReport> Reports()
    {
        lock (_mu)
        {
            var output = new List<LedgerReport>();
            foreach (var name in _finished)
            {
                if (_results[name].Ledger is { } ledger)
                {
                    output.AddRange(ledger.Reports);
                }
            }
            return output;
        }
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
