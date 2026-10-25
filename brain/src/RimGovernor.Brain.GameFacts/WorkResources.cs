using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Observations;

/// <summary>Current native ownership and inputs outside loose stock, captured with the colony frame.</summary>
public sealed record WorkResourcePawn(string Pawn, string Assignment, long Revision, string State,
    string CarriedThing, string CarriedResource, int CarriedCount)
{
    public required double LocalHour { get; init; }
    public string Bill { get; init; } = "";
    public string Target { get; init; } = "";
    public string Unfinished { get; init; } = "";
}
