namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The finished tasks waiting for a rounds review (#3624). When a coupled Method's worker finishes a task with bills left,
/// the worker records the task here and does not start that Method's next tasks; the scheduler's next step runs the rounds
/// review, which decides who takes the worker's next task, and clears the tasks it covered. The wait is the worker's alone:
/// the clock keeps running and the Method's other participants keep working.
/// </summary>
internal sealed class TaskReviews
{
    private readonly Lock _mu = new();

    // Each waiting task's key (Method, pawn and revision) and the order it was recorded in.
    private readonly Dictionary<string, long> _waiting = new(StringComparer.Ordinal);
    private long _sequence;

    /// <summary>Records a finished task that waits for the next review.</summary>
    public void Await(string key)
    {
        lock (_mu)
        {
            _waiting.TryAdd(key, ++_sequence);
        }
    }

    /// <summary>Whether the finished task <paramref name="key"/> still waits for a review.</summary>
    public bool Waiting(string key)
    {
        lock (_mu)
        {
            return _waiting.ContainsKey(key);
        }
    }

    /// <summary>The last task a review starting now covers, or null when none waits.</summary>
    public long? Pending()
    {
        lock (_mu)
        {
            return _waiting.Count == 0 ? null : _waiting.Values.Max();
        }
    }

    /// <summary>A review covering the tasks recorded through <paramref name="through"/> has committed: their workers go on.</summary>
    public void Reviewed(long through)
    {
        lock (_mu)
        {
            foreach (var key in _waiting.Where(w => w.Value <= through).Select(w => w.Key).ToArray())
            {
                _waiting.Remove(key);
            }
        }
    }
}
