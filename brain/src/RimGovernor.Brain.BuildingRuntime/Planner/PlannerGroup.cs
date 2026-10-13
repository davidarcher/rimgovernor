namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Planner priorities, the priority class the planner's goal usually carries in <c>DetectRounds</c>. A tight step budget is spent lowest-first.</summary>
internal static class PlannerPriority
{
    /// <summary>Naming, active combat.</summary>
    public const int Preempt = 0;

    /// <summary>Critical medicine, disaster recovery.</summary>
    public const int Critical = 1;

    /// <summary>Work, food, shelter, warmth, cooking, power, storage.</summary>
    public const int Foothold = 2;

    /// <summary>Maintenance and development.</summary>
    public const int Maintenance = 3;

    /// <summary>Comfort and expansion.</summary>
    public const int Comfort = 4;
}

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

    /// <summary>The cutoff came first (Go <c>errCutoff</c>).</summary>
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
/// <see cref="Go"/> only queues; <see cref="Start"/> runs the queue critical class first, then priority order
/// (stable, so queue order breaks ties) with at most <c>width</c> planners at once, taking a slot before the next
/// planner starts. Native reads execute one at a time on the game's main thread, so the wave overlaps round
/// trips, and the order slots are taken decides which goals' reads go first when the step budget is tight.
/// <see cref="WaitCriticalAsync"/> returns once every critical planner has returned; <see cref="WaitUntilAsync"/>
/// joins the rest up to a cutoff.
/// </remarks>
internal sealed class PlannerGroup
{
    /// <summary>Bounds how many planners a step runs at once. It matches the bridge's in-flight call cap so admission order, not gate queueing, decides whose native reads go first.</summary>
    public static int PlannerWidth => BridgeApi.MaxConcurrentCalls;

    private sealed record QueuedPlanner(string Name, PlannerClass Class, int Priority, Func<Task> Run);

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
    private int _criticalLeft;
    private bool _started;

    public PlannerGroup(CancellationToken ctx, int width)
    {
        _ctx = ctx;
        _width = Math.Max(1, width);
        _contextRegistration = ctx.Register(() => _contextDone.TrySetResult());
    }

    /// <summary>
    /// Queues <paramref name="fn"/> as the named planner of <paramref name="cls"/> at <paramref name="priority"/>.
    /// Each fn wraps its planner's error with the planner's name, so a recorded failure names its source.
    /// </summary>
    public void Go(string name, PlannerClass cls, int priority, Func<Task> fn)
    {
        _queued.Add(new QueuedPlanner(name, cls, priority, fn));
        _names.Add(name);
        _pending.Add(name);
        if (cls == PlannerClass.Critical)
        {
            _criticalLeft++;
        }
    }

    /// <summary>
    /// Runs the queue in the background: critical planners first, then by priority, each taking a slot before the
    /// next starts. A planner queued behind a finished step context is skipped, not run.
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
            .ThenBy(p => p.Priority)
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
        _ = Task.Run(() => RunQueueAsync(ordered));
    }

    private async Task RunQueueAsync(List<QueuedPlanner> ordered)
    {
        using var slots = new SemaphoreSlim(_width, _width);
        var running = new List<Task>();
        for (var i = 0; i < ordered.Count; i++)
        {
            try
            {
                await slots.WaitAsync(_ctx).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // the step context ended before a slot came free
            }
            if (_ctx.IsCancellationRequested)
            {
                for (var j = i; j < ordered.Count; j++)
                {
                    Finish(ordered[j], null);
                }
                break;
            }
            var planner = ordered[i];
            running.Add(Task.Run(async () =>
            {
                try
                {
                    Exception? error = null;
                    try
                    {
                        await planner.Run().ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        error = e;
                    }
                    Finish(planner, error);
                }
                finally
                {
                    slots.Release();
                }
            }));
        }
        await Task.WhenAll(running).ConfigureAwait(false);
        _all.TrySetResult();
        _contextRegistration.Dispose();
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
