using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire.Common;
using Identity = RimGovernor.Contracts.Wire.Common.Identity;
using Cell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.Contracts.Wire.Observations;

public sealed record FoodActivityRequest(Identity Identity, EquatableArray<string> Plants, PawnActivityOrder? Existing = null);
public sealed record FoodActivityOption(string Pawn, string Target, string Resource, Cell Origin, Cell Destination,
    string WorkGiver, int Count, double TravelTicks, double WorkTicks);
public sealed record FoodActivityRead
{
    public ObservationContext? Context { get; init; }
    public bool Known { get; init; }
    public bool Truncated { get; init; }
    public EquatableArray<FoodActivityOption> Options { get; init; }
    public RimGovernor.Contracts.Wire.Operations.PawnActivityExecution? Execution { get; init; }
    public bool Carrying { get; init; }
}
