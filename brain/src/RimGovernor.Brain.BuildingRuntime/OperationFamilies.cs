namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// The fact families a dispatched action of kind changes (Go <c>operationFamilies</c>, clock_facts.go:96).
    /// Everything is true when the kind is not known or has no narrower answer: the whole decoded store is dropped.
    /// A building write changes the colony census, the room census and the pawns the builders are.
    /// </summary>
    internal static (bool Everything, IReadOnlyList<Pol.FactFamily> Families) OperationFamilies(ActionKind kind, bool known)
    {
        if (!known)
        {
            return (true, []);
        }
        return kind == ActionKind.Building
            ? (false, [Pol.FactFamily.Colony, Pol.FactFamily.Rooms, Pol.FactFamily.Pawns])
            : (true, []);
    }
}

/// <summary>
/// Go <c>bridge.WithDeferredSnapshot</c> / <c>bridge.DeferredSnapshot</c> (snapshot_flush.go:19-26): marks the
/// Actions and Apply calls issued under it defer_snapshot, so native records the write without capturing a frame
/// until the flush. The mark follows the async flow like a Go context value.
/// </summary>
internal static class DeferredSnapshotScope
{
    private static readonly AsyncLocal<bool> s_deferred = new();

    /// <summary>Whether the flow carries the mark (Go <c>DeferredSnapshot(ctx)</c>).</summary>
    internal static bool Current => s_deferred.Value;

    /// <summary>Marks the flow until the returned scope is disposed.</summary>
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
