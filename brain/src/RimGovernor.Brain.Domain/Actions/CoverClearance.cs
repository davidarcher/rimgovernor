namespace RimGovernor.Brain.Domain;

/// <summary>
/// Cover clearance designations: the native designation defs the game's own designators place (Mine on a
/// mineable, CutPlant on a plant, Haul on a chunk, Deconstruct on a building). The wire name is the string.
/// </summary>
public enum CoverClearanceDesignation
{
    [WireName("Mine")]
    Mine,

    [WireName("CutPlant")]
    CutPlant,

    [WireName("Haul")]
    Haul,

    [WireName("Deconstruct")]
    Deconstruct,
}

/// <summary>
/// Designates one observed cover thing for removal: the exact thing by identity at its cell, with the one
/// designation its kind needs. The designation is the whole write; ordinary work removes the thing. A Haul on an
/// ordinary stack (not designateHaulable) stands a native haul-out record instead, a removal with no designation (#3625).
/// </summary>
public sealed record CoverClearance : IActionPayload
{
    /// <summary>Throws <see cref="DomainException"/> when invalid.</summary>
    public CoverClearance(string thing, string definition, CoverClearanceDesignation designation, Cell cell)
    {
        DomainException.Require(
            IdText.IsValidText(thing) && IdText.IsValidText(definition) && Wire.IsDefined(designation) && cell.X >= 0 && cell.Z >= 0,
            "invalid cover clearance identity, designation or cell"
        );
        Thing = thing;
        Definition = definition;
        Designation = designation;
        Cell = cell;
    }

    /// <summary>The string names one of the four designations.</summary>
    public static bool ValidCoverClearanceDesignation(string designation) => Wire.TryParse<CoverClearanceDesignation>(designation, out _);

    public string Thing { get; }

    public string Definition { get; }

    public CoverClearanceDesignation Designation { get; }

    public Cell Cell { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewCoverClearanceAction(ActionId id, CoverClearance clearance) =>
        Create(id, ActionKind.CoverClearance, clearance);

    /// <summary>The payload when this is a cover clearance action, otherwise null.</summary>
    public CoverClearance? CoverClearance => PayloadOf<CoverClearance>(ActionKind.CoverClearance);
}
