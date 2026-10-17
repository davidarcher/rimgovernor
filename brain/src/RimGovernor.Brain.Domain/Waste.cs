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

public static class WasteExtensions
{
    /// <summary>Go <c>CorpseOf.Valid</c>: one of the three classes.</summary>
    public static bool IsValid(this CorpseOf corpse) => corpse is CorpseOf.Colonist or CorpseOf.Stranger or CorpseOf.Animal;
}
