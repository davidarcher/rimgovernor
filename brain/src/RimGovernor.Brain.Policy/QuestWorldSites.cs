using RimGovernor.Brain.Domain;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record SiteHomeRoute
{
    public int Tile { get; init; }

    public Fact<bool> Reachable { get; init; }

    public Fact<long> TravelTicks { get; init; }
}


public sealed record PeaceTalksPawn
{
    public PawnId Id { get; init; }

    public Fact<int> Social { get; init; }

    public Fact<bool> Leader { get; init; }
}
