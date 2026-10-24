namespace RimGovernor.Brain.Policy;

/// <summary>Derived once alongside shared demand; observed claims are not native reservations.</summary>
public sealed record ForecastWorkInputs
{
    public Tick Tick { get; init; }
    public Fact<EquatableArray<WorkPawn>> Pawns { get; init; }
    public EquatableArray<IngredientReservation> IngredientReservations { get; init; }

    /// <summary>The observed work roster and bill reservations at the review tick.</summary>
    public static ForecastWorkInputs ForecastWorkInputsOf(RoundsFacts facts) => new()
    {
        Tick = facts.Tick, Pawns = facts.ForecastObserved.WorkPawns, IngredientReservations = facts.BillReservations,
    };
}
