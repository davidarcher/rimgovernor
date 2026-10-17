namespace RimGovernor.Brain.Bridge.Contracts;

public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>bridge.StepRequest</c>: what a scheduler step, the colony event consumer or a renewal opens with. The current
    /// scope always comes back; the owned clock status comes on request. Declared nested here because the Go package
    /// qualifier <c>bridge.</c> is the alias <c>BridgeApi</c> of this class.
    /// </summary>
    public sealed class StepRequest
    {
        public bool ClockStatus { get; init; }
    }
}
