namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>bridge.StepRequest</c>: what a scheduler step, the colony event consumer or a renewal opens with. The current
/// scope always comes back; the owned clock status comes on request.
/// </summary>
public sealed class StepRequest
{
    public bool ClockStatus { get; init; }
}
