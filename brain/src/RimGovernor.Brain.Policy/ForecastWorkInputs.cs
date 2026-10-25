namespace RimGovernor.Brain.Policy;

/// <summary>Derived once alongside shared demand; observed claims are not native reservations.</summary>
public sealed record ForecastWorkInputs
{
    public Tick Tick { get; init; }
    public WorkPawnsRead Pawns { get; init; } = new WorkPawnsRead([]);
    public EquatableArray<IngredientReservation> IngredientReservations { get; init; }

    /// <summary>The observed work roster and bill reservations at the review tick.</summary>
    public static ForecastWorkInputs ForecastWorkInputsOf(ReviewFacts facts) => new()
    {
        Tick = facts.Tick, Pawns = facts.ForecastObserved.WorkPawns, IngredientReservations = facts.BillReservations,
    };
}
