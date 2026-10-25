namespace RimGovernor.Brain.Policy;

public sealed partial record PlanningDefinition
{

    public string Name { get; init; } = "";

    public PlanningAvailability Availability { get; init; } = PlanningAvailability.NotInCatalog;

    public bool Available
    {
        get => Availability == PlanningAvailability.Ready;
        init => Availability = value ? PlanningAvailability.Ready : PlanningAvailability.ResearchLocked;
    }

    public int ConstructionSkill { get; init; }

    public bool NeedsPower { get; init; }

    /// <summary>True for a Building_MechCharger definition.</summary>
    public bool MechCharger { get; init; }

    /// <summary>The native definition uses Building_NutrientPasteDispenser.</summary>
    public bool PasteDispenser { get; init; }

/// <summary>The ResearchProjectDefs the definition requires, as the native definition declares them (unfinished ones make it unavailable).</summary>
    public IReadOnlyList<string> Research { get; init; } = [];

    public Bounds Size { get; init; }

    /// <summary>Floor definition facts: Terrain marks a TerrainDef and the stats are what a laid floor carries.</summary>
    public bool Terrain { get; init; }

    /// <summary>The def's own pathCost: what a pawn pays to walk over the built thing or the laid floor.</summary>
    public int PathCost { get; init; }

    /// <summary>
    /// Whether the def is made from stuff (it has stuff categories). A stuffed def has no single cost list:
    /// FixedConstruction is absent and StuffChoice prices it from StuffOptions.
    /// </summary>
    public bool Stuffed { get; init; }

    /// <summary>
    /// Every allowed stuff with its cost list and stat values, ordered by defName; empty for a definition not
    /// made from stuff, and for a stuffed one nothing is allowed to make it from.
    /// </summary>
    public IReadOnlyList<StuffOption> StuffOptions { get; init; } = [];

    /// <summary>The room-role furniture roles the native catalog assigns the definition (FurnitureRole names), sorted.</summary>
    public IReadOnlyList<string> RoomRoles { get; init; } = [];
}

/// <summary>One material a stuffed definition may be built from.</summary>
public sealed record StuffOption
{
    public string Stuff { get; init; } = "";

    public IReadOnlyList<Amount> Costs { get; init; } = [];

    /// <summary>The market value of Costs: what the cheapest criterion ranks by.</summary>
    public double Value { get; init; }

    /// <summary>
    /// Whether the game generates the stuff as an ordinary material (StuffProperties.commonality above zero).
    /// Bioferrite is not: it is only ever made by an Anomaly colony, so a def is never planned from it while an
    /// ordinary stuff is allowed.
    /// </summary>
    public bool Common { get; init; }

    /// <summary>The stat values the game shows for the def made of this stuff (the StuffChoice criteria); a stat the game does not show is absent.</summary>
    public IReadOnlyDictionary<string, double> Stats { get; init; } = new Dictionary<string, double>();
}

