using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance;

/// <summary>A closed list of production horizons; adding one requires its production definition and charter review.</summary>
public enum ProductionObservationHorizon { Unspecified, CoupledForecast }

/// <summary>Observes a production horizon that cannot be staged away without invalidating the native claim.</summary>
public sealed record HorizonObservation
{
    public required ProductionObservationHorizon Horizon { get; init; }
    public required string ClaimReason { get; init; }
    public required int ObservationSlackTicks { get; init; }
    public required string ObservationSlackReason { get; init; }

    public int HorizonTicks => Horizon switch
    {
        ProductionObservationHorizon.CoupledForecast => checked((int)(ForecastCoupled.TierOneHorizonDays * GameTime.TicksPerDay)),
        _ => 0,
    };

    public int Budget => checked(HorizonTicks + ObservationSlackTicks);

    public string? Problem() => HorizonTicks <= 0 ? "declare a supported production observation horizon"
        : string.IsNullOrWhiteSpace(ClaimReason) ? "explain why staging a later precondition invalidates the horizon claim"
        : ObservationSlackTicks <= 0 || ObservationSlackTicks > TypedCase.StagedTickCap ? "observation slack must be positive and at most the ordinary staged tick cap"
        : string.IsNullOrWhiteSpace(ObservationSlackReason) ? "justify the bounded observation slack"
        : null;
}
