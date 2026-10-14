namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>Current native ownership and inputs outside loose stock, captured with the colony frame.</summary>
public sealed record WorkResourcePawn(string Pawn, string Assignment, long Revision, string State,
    string CarriedThing, string CarriedResource, int CarriedCount)
{
    public double? LocalHour { get; init; }
    public string Bill { get; init; } = "";
    public string Target { get; init; } = "";
}
