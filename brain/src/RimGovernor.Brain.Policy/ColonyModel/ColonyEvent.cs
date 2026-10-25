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

    /// <summary>The medicine crafted toward the stock target reaches it ("target") or its ingredients run out ("ingredients", #3868).</summary>
    MedicineCraft,

    /// <summary>
    /// The medicine the routine trade buys changes (#3907): the reserve's latch starts it ("start") or the stock reaches the target
    /// that stops it ("stop"), the silver is spent ("silver"), a comp's visits begin ("visits"), or the best kind, which the latch does not
    /// count, runs out ("best").
    /// </summary>
    MedicineTrade,
    /// <summary>A scheduled incident's initial wound batch or one disease tend consumes medicine.</summary>
    MedicineTreatment,
    HorizonEnd,
}

/// <summary>One event on the trajectory: when it fired (days from now), what it was, and every stock level just after it.</summary>
public sealed record ModelEvent
{
    public double Day { get; init; }

    public ModelEventKind Kind { get; init; }

    /// <summary>The stock that reached zero, the scheduled effect's id, why medicine crafting changed (target, ingredients), or empty for a boundary, the food runway and the horizon.</summary>
    public string Id { get; init; } = "";

    /// <summary>Indexed by <see cref="ModelStock"/>.</summary>
    public EquatableArray<double> Levels { get; init; }

    /// <summary>The medicine on hand by kind, indexed by <see cref="ColonyTrajectory.MedicineKinds"/>; they sum to the medicine level.</summary>
    public EquatableArray<double> Medicine { get; init; }

    /// <summary>Medicine consumed at this event, by kind. Added back when reading the continuous interval ending just before this event.</summary>
    public EquatableArray<double> MedicineConsumed { get; init; }
}
