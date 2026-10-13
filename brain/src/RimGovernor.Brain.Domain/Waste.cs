namespace RimGovernor.Brain.Domain;

/// <summary>
/// A corpse's inner pawn class, as the waste census reports it and a corpse bill's ingredient filter names it.
/// <see cref="None"/> is Go's empty string (no class named).
/// </summary>
public enum CorpseOf
{
    [WireName("")]
    None = 0,

    /// <summary>A player-faction humanlike.</summary>
    Colonist,

    /// <summary>Any other humanlike: raiders, visitors, prisoners.</summary>
    Stranger,

    /// <summary>Any non-humanlike.</summary>
    Animal,
}

/// <summary>
/// A thing's rot stage, as the waste census reports it and a corpse bill's minimum names it. <see cref="None"/>
/// (Go's empty string) is unknown (or, on a bill, any stage).
/// </summary>
public enum RotStage
{
    [WireName("")]
    None = 0,
    Fresh,
    Rotting,
    Dessicated,
}

public static class WasteExtensions
{
    /// <summary>Go <c>CorpseOf.Valid</c>: one of the three classes.</summary>
    public static bool IsValid(this CorpseOf corpse) => corpse is CorpseOf.Colonist or CorpseOf.Stranger or CorpseOf.Animal;

    /// <summary>Go <c>RotStage.Valid</c>: one of the three stages.</summary>
    public static bool IsValid(this RotStage stage) => stage is RotStage.Fresh or RotStage.Rotting or RotStage.Dessicated;

    /// <summary>Go <c>RotStage.Spoiled</c>: a corpse past fresh, rotting or desiccated.</summary>
    public static bool Spoiled(this RotStage stage) => stage is RotStage.Rotting or RotStage.Dessicated;
}
