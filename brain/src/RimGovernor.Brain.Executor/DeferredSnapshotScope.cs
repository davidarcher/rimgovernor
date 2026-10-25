namespace RimGovernor.Brain.Executor;

/// <summary>Marks writes that share one final native snapshot flush. The mark follows the async flow.</summary>
internal static class DeferredSnapshotScope
{
    private static readonly AsyncLocal<bool> s_deferred = new();
    internal static bool Current => s_deferred.Value;
    internal static IDisposable Begin()
    {
        var previous = s_deferred.Value;
        s_deferred.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => s_deferred.Value = previous;
    }
}
