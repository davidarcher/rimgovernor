namespace RimGovernor.Brain.Policy;

// Go: clearance.go

/// <summary>
/// A complete native building observation. Admission remains subject to a fresh observation and the native
/// counterfactual roof check.
/// </summary>
public sealed record ClearanceTarget
{
    public string EntityId { get; init; } = "";

    public string DefName { get; init; } = "";

    public Cell Minimum { get; init; }

    public Cell Maximum { get; init; }

    public string Class { get; init; } = "";

    public bool Deconstructible { get; init; }

    public bool AncientDanger { get; init; }

    public string RoofBlocker { get; init; } = "";

    /// <summary>
    /// A standing Deconstruct designation, whoever placed it. It suppresses another write; the building
    /// remains work until removed.
    /// </summary>
    public bool Designated { get; init; }

    /// <summary>
    /// Marks the colony's own building, reported only on planned ground (#1365); EnclosesRoom is then a wall
    /// or door bounding an indoor room.
    /// </summary>
    public bool Player { get; init; }

    public bool EnclosesRoom { get; init; }

    /// <summary>
    /// Stamped by the clearance planner (#2103): the def packs into a minified item, and the piece is an
    /// owned bed or a bench with an active bill.
    /// </summary>
    public bool Packable { get; init; }

    public bool InUse { get; init; }

    public SalvageEvidence? Salvage { get; init; }

    /// <summary>Native ran out of salvage time budget before computing this row; Salvage is absent because it was not computed.</summary>
    public bool SalvageSkipped { get; init; }

    /// <summary>The stack a foreign item holds (#2270); zero when unknown.</summary>
    public long Count { get; init; }
}

/// <summary>One native clearance read: the buildings in Home and the planned ground's constructed floors.</summary>
public sealed record ClearanceCensus
{
    public EquatableArray<ClearanceTarget> Targets { get; init; }

    /// <summary>The constructed floor cells on the planned ground the read asked for (#1365); none without it.</summary>
    public EquatableArray<ClearanceFloor> Floors { get; init; }
}

public static class ClearanceRules
{
    /// <summary>
    /// The census row's own native verdict, wherever it stands: empty when nothing about the thing itself
    /// refuses its removal.
    /// </summary>
    public static string ClearanceHoldReason(ClearanceTarget row)
    {
        if (!row.Deconstructible)
        {
            return "not_deconstructible";
        }

        if (row.RoofBlocker.Length != 0)
        {
            return "roof_blocker";
        }

        if (row.AncientDanger)
        {
            return "ancient_danger";
        }

        if (row.Class == "ancient_casket")
        {
            return "casket";
        }

        return "";
    }

}
