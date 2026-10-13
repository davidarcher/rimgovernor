namespace RimGovernor.Brain.Policy;

/// <summary>
/// One row of the upkeep room census: a room holding a colonist bed or carrying a common role (DiningRoom,
/// RecRoom), with its native quality stats (unknown when the read omitted any of them). Beds lists the
/// colonist beds (humanlike, not medical, not for prisoners) standing in the room; a bed appears in at most
/// one room.
/// </summary>
public sealed record UpkeepRoom
{
    public string Id { get; init; } = "";

    /// <summary>The native RoomRoleDef name, empty when the room has none.</summary>
    public string Role { get; init; } = "";

    public Fact<RoomQuality> Quality { get; init; }

    public Fact<int> Cells { get; init; }

    public EquatableArray<string> Beds { get; init; }
}

/// <summary>
/// A colonist's most senior royal title and the bedroom requirements RoyalTitleDef sets for its holder.
/// Requirements an ideo precept disables are left out, and an ascetic holder has none. Zero values mean no
/// such requirement.
/// </summary>
public sealed record RoyalTitle
{
    public string Definition { get; init; } = "";

    public int Seniority { get; init; }

    public int BedroomMinArea { get; init; }

    public int BedroomMinImpressiveness { get; init; }

    /// <summary>Requires every room cell to carry a floor terrain.</summary>
    public bool BedroomFloored { get; init; }

    /// <summary>The required furniture: each entry is met by Count things of any one of its definitions.</summary>
    public EquatableArray<BedroomThing> BedroomThings { get; init; }
}

/// <summary>One royal bedroom furniture requirement.</summary>
public sealed record BedroomThing
{
    public EquatableArray<Resource> AnyOf { get; init; }

    public int Count { get; init; }
}
