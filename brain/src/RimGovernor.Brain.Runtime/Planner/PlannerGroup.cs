namespace RimGovernor.Brain.Runtime;

/// <summary>
/// Says whether a clock admission waits on the planner. The admission cycle joins the critical class only:
/// authority, emergency evidence and the planners whose verdict the window decision reads. The optional class is
/// the development reviews; they run on the same snapshot and an evaluation still running at the cutoff is
/// recorded missed, never waited for.
/// </summary>
public enum PlannerClass
{
    Critical,
    Optional,
}

/// <summary>What ended <see cref="PlannerGroup.WaitCriticalAsync"/>.</summary>
internal enum CriticalWait
{
    /// <summary>Every critical planner returned.</summary>
    Done,

    /// <summary>The cutoff came first.</summary>
    Cutoff,
}

/// <summary>
/// Runs one clock step's routine planners as a concurrent wave, but a planner's failure is isolated: it is
/// recorded in <see cref="Failures"/> instead of cancelling its peers or failing the wait. One broken planner (a
/// refused native read, a missing tool) must not stop the whole clock step from reaching the window decision, or
/// the clock never starts while that family is composed. The step's own token is the only thing that aborts the
/// wave: once it is done, every planner fails for the same reason and the wait throws that error.
/// </summary>
/// <remarks>
/// <see cref="Go"/> only queues; <see cref="Start"/> runs the queue critical class first, then in queue order
/// with at most <c>width</c> planners at once. A planner waits for its selected prerequisites before taking a
/// slot; independent planners take slots in queue order. Native reads execute one at a time on the game's main thread, so the wave overlaps round
/// trips, and the order slots are taken decides which goals' reads go first when the step budget is tight.
/// <see cref="WaitCriticalAsync"/> returns once every critical planner has returned; <see cref="WaitUntilAsync"/>
/// joins the rest up to a cutoff.
/// </remarks>
internal sealed class PlannerGroup
{
    /// <summary>Bounds how many planners a step runs at once. It matches the bridge's in-flight call cap so admission order, not gate queueing, decides whose native reads go first.</summary>
    public static int PlannerWidth => CombatCalls.MaxConcurrentCalls;

    private sealed record QueuedPlanner(string Name, PlannerClass Class, Func<Task> Run, IReadOnlyList<string> After);

    private readonly CancellationToken _ctx;
    private readonly int _width;
    private readonly object _mu = new();
    private readonly TaskCompletionSource _contextDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _critical = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _contextRegistration;
    private List<QueuedPlanner> _queued = [];
    private readonly List<Exception> _failures = [];
    // Every planner queued, in queue order; pending those not yet returned (or skipped); criticalLeft counts the
    // critical ones among them and _critical completes when it reaches zero.
    private readonly List<string> _names = [];
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _done = new(StringComparer.Ordinal);
    private int _criticalLeft;
    private bool _started;

    public PlannerGroup(int width, CancellationToken ctx)
    {
        _ctx = ctx;
        _width = Math.Max(1, width);
        _contextRegistration = ctx.Register(() => _contextDone.TrySetResult());
    }

    /// <summary>
    /// Queues <paramref name="fn"/> as the named planner of <paramref name="cls"/>.
    /// Each fn wraps its planner's error with the planner's name, so a recorded failure names its source.
    /// <paramref name="after"/> names selected runs whose claims must settle first, even if they fail.
    /// </summary>
    public void Go(string name, PlannerClass cls, Func<Task> fn, IReadOnlyList<string>? after = null)
    {
        _queued.Add(new QueuedPlanner(name, cls, fn, after ?? []));
        _done.Add(name, new(TaskCreationOptions.RunContinuationsAsynchronously));
        _names.Add(name);
        _pending.Add(name);
        if (cls == PlannerClass.Critical)
        {
            _criticalLeft++;
        }
    }

    /// <summary>
    /// Runs the queue in the background: critical planners first, then in queue order, subject to their selected
    /// prerequisites. A planner queued behind a finished step context is skipped, not run.
    /// </summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        var ordered = _queued
            .OrderBy(p => p.Class == PlannerClass.Critical ? 0 : 1)
            .ToList();
        _queued = [];
        if (_criticalLeft == 0)
        {
            _critical.TrySetResult();
        }
        if (ordered.Count == 0)
        {
            _all.TrySetResult();
            return;
        }
        _ = Task.Run(async () => await RunQueueAsync(ordered).ConfigureAwait(false));
    }

    private async Task RunQueueAsync(List<QueuedPlanner> ordered)
    {
        using var slots = new SemaphoreSlim(_width, _width);
        var running = new List<Task>();
        foreach (var planner in ordered)
        {
            running.Add(RunPlannerAsync(planner, slots));
        }
        await Task.WhenAll(running).ConfigureAwait(false);
        _all.TrySetResult();
        _contextRegistration.Dispose();
    }

    private async Task RunPlannerAsync(QueuedPlanner planner, SemaphoreSlim slots)
    {
        var entered = false;
        Exception? error = null;
        try
        {
            // A dependency waits outside the width limit: defense is queued before medical, and must not
            // occupy the last slot while waiting for medical's claim to settle. Unselected owners have no run.
            await Task.WhenAll(planner.After.Where(_done.ContainsKey).Select(name => _done[name].Task)).WaitAsync(_ctx).ConfigureAwait(false);
            await slots.WaitAsync(_ctx).ConfigureAwait(false);
            entered = true;
            await Task.Run(planner.Run, _ctx).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_ctx.IsCancellationRequested)
        {
            // The step ended before this planner could finish or take a slot.
            return;
        }
        catch (Exception e)
        {
            error = e;
        }
        finally
        {
            Finish(planner, error);
            if (entered)
            {
                slots.Release();
            }
        }
    }

    /// <summary>Records a planner's return (or skip) and releases the waiters it was the last of.</summary>
    private void Finish(QueuedPlanner planner, Exception? error)
    {
        lock (_mu)
        {
            if (error is not null)
            {
                _failures.Add(error);
            }
            _pending.Remove(planner.Name);
            _done[planner.Name].TrySetResult();
            if (planner.Class == PlannerClass.Critical && --_criticalLeft == 0)
            {
                _critical.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Starts the wave if needed and waits until every critical planner has returned, the step token ends, or
    /// <paramref name="cutoff"/> completes. Throws the step token's error (with the isolated failures) when that
    /// is what cut the wave short; returns <see cref="CriticalWait.Cutoff"/> when the cutoff came first.
    /// </summary>
    public async Task<CriticalWait> WaitCriticalAsync(Task cutoff)
    {
        Start();
        var first = await Task.WhenAny(_critical.Task, _contextDone.Task, cutoff).ConfigureAwait(false);
        if (first == _critical.Task)
        {
            return CriticalWait.Done;
        }
        if (first == _contextDone.Task)
        {
            await _critical.Task.ConfigureAwait(false);
            ContextError();
            return CriticalWait.Done;
        }
        return CriticalWait.Cutoff;
    }

    /// <summary>
    /// Waits until every planner has returned or <paramref name="cutoff"/> completes, and returns the names of the
    /// planners still pending, in queue order (none when the wave completed).
    /// </summary>
    public async Task<IReadOnlyList<string>> WaitUntilAsync(Task cutoff)
    {
        Start();
        var first = await Task.WhenAny(_all.Task, cutoff).ConfigureAwait(false);
        return first == _all.Task ? [] : Pending();
    }

    private void ContextError()
    {
        if (_ctx.IsCancellationRequested)
        {
            var errors = new List<Exception> { new OperationCanceledException(_ctx) };
            errors.AddRange(Failures());
            throw new AggregateException(errors);
        }
    }

    /// <summary>Names the planners not yet returned, in queue order.</summary>
    public IReadOnlyList<string> Pending()
    {
        lock (_mu)
        {
            return _names.Where(_pending.Contains).ToList();
        }
    }

    /// <summary>Lists every isolated planner failure of this wave, in completion order.</summary>
    public IReadOnlyList<Exception> Failures()
    {
        lock (_mu)
        {
            return [.. _failures];
        }
    }
}
