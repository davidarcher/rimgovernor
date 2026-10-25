namespace RimGovernor.Brain.Policy;

/// <summary>Derived once alongside shared demand; observed claims are not native reservations.</summary>
public sealed record ForecastWorkInputs
{
    public Tick Tick { get; init; }

    /// <summary>The review tick shared by demand and forecast inputs.</summary>
    public static ForecastWorkInputs ForecastWorkInputsOf(ReviewFacts facts) => new()
    {
        Tick = facts.Tick,
    };
}
