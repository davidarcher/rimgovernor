namespace RimGovernor.Brain.Policy;

// The tier-2 colony model's event vocabulary (#3529 of #3470): the stocks it moves, the kinds of event it jumps between and one
// event on the trajectory.

/// <summary>The stocks of v1, the index into every level and rate array of the engine. Power is out (no per-building demand read).</summary>
public enum ModelStock
{
    Nutrition,
    Materials,
    Medicine,
    ResearchPoints,
}

/// <summary>
/// Event kinds in tiebreak order: events at the same time fire by kind, then by id. A twelfth boundary fires first so the rates
/// are re-evaluated before a stock is tested against them; the horizon end fires last.
/// </summary>
public enum ModelEventKind
{
    TwelfthBoundary,
    ProjectCompletion,
    Harvest,
    StockZero,
    FoodRunway,
    HorizonEnd,
}

/// <summary>One event on the trajectory: when it fired (days from now), what it was, and every stock level just after it.</summary>
public sealed record ModelEvent
{
    public double Day { get; init; }

    public ModelEventKind Kind { get; init; }

    /// <summary>The stock that reached zero, the scheduled effect's id, or empty for a boundary, the food runway and the horizon.</summary>
    public string Id { get; init; } = "";

    /// <summary>Indexed by <see cref="ModelStock"/>.</summary>
    public EquatableArray<double> Levels { get; init; }
}
