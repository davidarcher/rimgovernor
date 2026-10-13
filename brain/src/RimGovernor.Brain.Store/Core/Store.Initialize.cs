namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>
    /// The reads a freshly opened journal must survive beyond its table probes: the control row, when there is
    /// one, must decode (Go <c>initialize</c>'s closing <c>currentControl</c>).
    /// </summary>
    private static void InitializeChecks(StoreTransaction tx) => _ = CurrentControlIn(tx);
}
