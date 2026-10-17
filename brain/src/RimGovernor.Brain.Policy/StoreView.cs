namespace RimGovernor.Brain.Policy;

// The colony view the departments declare their stores from (DeclareStores). Declarations are derived state,
// recomputed every pass in memory; the standing zones are the only record, and the admin/stockpiles Need applies
// the diff.

/// <summary>
/// The one-cell meal store by the dining table, resolved by the caller from the dining facts: the table is a
/// built-furniture fact, so this is the one store sited from more than the layout plan.
/// </summary>
public sealed record MealStore
{
    /// <summary>The planned dining room's interior.</summary>
    public Rectangle Dining { get; init; }

    public required StockpileFilter Filter { get; init; }

    /// <summary>Places the cell: nearest Anchor, off Avoid (the chairs).</summary>
    public Cell Anchor { get; init; }

    public IReadOnlyList<Cell> Avoid { get; init; } = [];

    /// <summary>States the colony no longer wants a warm spot by the table.</summary>
    public bool Retired { get; init; }
}

/// <summary>
/// The colony view the departments read. Layout and Rooms are null while the layout plan or the room census is
/// unknown, and no store that needs them is declared.
/// </summary>
public sealed partial record StoreView
{
    public Bounds Bounds { get; init; }

    public IReadOnlyList<SiteCell> Cells { get; init; } = [];

    public IReadOnlyList<Cell> Protected { get; init; } = [];

    public LayoutPlan? Layout { get; init; }

    public RoomObservation? Rooms { get; init; }

    /// <summary>Null when the table meal store's facts are unknown.</summary>
    public MealStore? Meals { get; init; }

    /// <summary>
    /// The benches consuming stored inputs, and Benches the benches standing (unknown holds every bench store);
    /// both are read by the Industry department's stores.
    /// </summary>
    public IReadOnlyList<BenchInput> BenchInputs { get; init; } = [];

    public Fact<IReadOnlyDictionary<string, bool>> Benches { get; init; }

    /// <summary>The piece shapes the hospital template plans its beds with (the medicine store sits nearest them).</summary>
    public PieceShapes Shapes { get; init; } = new();

    /// <summary>The gear stores' filters; null while the catalog's armor split is unread (see GearStore).</summary>
    public GearStore? Gear { get; init; }

    /// <summary>The standing stockpile zones, read for the warehouse siting.</summary>
    public IReadOnlyList<StockpileZone> Zones { get; init; } = [];

    /// <summary>The stands the range should hold (see TrainingStands); the Military store asks for a training range while it is open.</summary>
    public Fact<int> TrainingStands { get; init; }

    /// <summary>The markers the sparring ring should hold (see TrainingRings); asked while two fighters are below the melee ceiling.</summary>
    public Fact<int> TrainingRings { get; init; }
}

/// <summary>
/// The departments' signal to layout for further rooms; the departments never plan them, layout adds them.
/// </summary>
public sealed record RoomDemand
{
    /// <summary>The stands the plan's training range should hold, 0 for no demand (the training concern, #2619).</summary>
    public int Ranges { get; init; }

    /// <summary>The markers the plan's sparring ring should hold, 0 for no demand (PlannedRoom.Markers mirrors it).</summary>
    public int Rings { get; init; }

    /// <summary>The materials yards the plan should hold, 0 for no demand (a further yard; see YardRoomsWanted).</summary>
    public int Yard { get; init; }

}
