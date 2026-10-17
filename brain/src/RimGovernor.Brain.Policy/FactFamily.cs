namespace RimGovernor.Brain.Policy;

/// <summary>
/// Names the observation facts native reports changed together (ObservationInvalidated).
/// </summary>
public enum FactFamily
{
    Definitions,
    World,
    Identity,
    Colony,
    Pawns,
    Emergency,
    Rooms,
    Research,
}

public static partial class PolicyFunctions
{
    /// <summary>Lists every family, in wire order.</summary>
    public static IReadOnlyList<FactFamily> FactFamilies() =>
        [FactFamily.Definitions, FactFamily.World, FactFamily.Identity, FactFamily.Colony, FactFamily.Pawns, FactFamily.Emergency, FactFamily.Rooms, FactFamily.Research];
}
