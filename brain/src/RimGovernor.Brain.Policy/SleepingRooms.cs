namespace RimGovernor.Brain.Policy;

/// <summary>Catalog stat names, supplied through compiler-checked native definition members.</summary>
public sealed record RoomQualityStatKeys(string Space, string Beauty, string Cleanliness, string Wealth, string Impressiveness);

/// <summary>
/// One row of the upkeep room census: a room holding a colonist bed or carrying a common role (DiningRoom,
/// RecRoom), with its native quality stats. Beds lists the
/// colonist beds (humanlike, not medical, not for prisoners) standing in the room; a bed appears in at most
/// one room.
/// </summary>
public sealed record UpkeepRoom
{
    public required RimGovernor.Brain.GameFacts.Observations.RoomState Native { get; init; }
    public required RoomQualityStatKeys StatKeys { get; init; }
    public string Id => Native.Id;
    public RoomRole Role => Native.Role;
    public int Cells => checked((int)Native.CellCount);
    public RoomQuality Quality => new(Stat(StatKeys.Space), Stat(StatKeys.Beauty),
        Stat(StatKeys.Cleanliness), Stat(StatKeys.Wealth), Stat(StatKeys.Impressiveness));

    private double Stat(string name) => Native.Stats.FirstOrDefault(stat => stat.DefName == name)?.Value
        ?? throw new DomainException($"room {Native.Id} carries no {name} stat");

    public required EquatableArray<string> Beds { get; init; }
}

/// <summary>
/// The bedroom requirements RoyalTitleDef sets for the holder of a colonist's most senior royal title.
/// Requirements an ideo precept disables are left out, and an ascetic holder has none. Zero values mean no
/// such requirement.
/// </summary>
public sealed record RoyalTitle
{
    public int BedroomMinArea { get; init; }

    public int BedroomMinImpressiveness { get; init; }

    /// <summary>The required furniture: each entry is met by Count things of any one of its definitions.</summary>
    public EquatableArray<BedroomThing> BedroomThings { get; init; }
}

/// <summary>One royal bedroom furniture requirement.</summary>
public sealed record BedroomThing
{
    public EquatableArray<Resource> AnyOf { get; init; }

    public int Count { get; init; }
}
