namespace RimGovernor.Brain.Bridge.Contracts;

public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>bridge.StepRequest</c>: what a scheduler step, an event poll or a renewal opens with. The current
    /// scope always comes back; the owned clock status and the emergency census come on request. <see cref="Identity"/>,
    /// when set, is the world the caller expects; another one is refused. Declared nested here because the Go package
    /// qualifier <c>bridge.</c> is the alias <c>BridgeApi</c> of this class.
    /// </summary>
    public sealed class StepRequest
    {
        public Identity? Identity { get; init; }

        public bool ClockStatus { get; init; }

        public bool Emergency { get; init; }
    }
}
