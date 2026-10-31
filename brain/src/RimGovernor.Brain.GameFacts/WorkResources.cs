using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Observations;

/// <summary>Current native ownership and inputs outside loose stock, captured with the colony frame. <see cref="Method"/> and
/// <see cref="Reason"/> are the installed execution's own (as the continuation read carries them), so the review tells a native
/// job takeover from the order's own failure.</summary>
public sealed record WorkResourcePawn(string Pawn, string Assignment, long Revision, string State,
    string CarriedThing, string CarriedResource, int CarriedCount)
{
    public required double LocalHour { get; init; }
    public string Method { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Bill { get; init; } = "";
    public string Target { get; init; } = "";
    public string Unfinished { get; init; } = "";
    public RimGovernor.Brain.Domain.OrderedWorkObservation? OrderedWork { get; init; }
}
