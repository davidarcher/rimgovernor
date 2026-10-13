namespace RimGovernor.Brain.Policy;

// Go: waste.go.

/// <summary>Mirrors the native WasteLocation the wire carries for one item.</summary>
public enum WasteState
{
    [WireName("")]
    None,

    /// <summary>Exposed: a containment candidate.</summary>
    [WireName("exposed")]
    Exposed,

    /// <summary>Relocated: already hauled to a separated stockpile.</summary>
    [WireName("relocated")]
    Relocated,

    /// <summary>Buried: already interred in a grave.</summary>
    [WireName("buried")]
    Buried,
}

/// <summary>
/// One native waste census row (thingId/kind/eligible/state). Eligible is native authority's own eligibility
/// judgment (destination separation, protection, player policy); policy never second-guesses it, only filters on it.
/// </summary>
public sealed record WasteItem
{
    public string Id { get; init; } = "";

    public string Kind { get; init; } = "";

    public WasteState State { get; init; }

    public bool Eligible { get; init; }

    public Cell Cell { get; init; }

    /// <summary>A corpse's inner pawn class; empty for anything else.</summary>
    public CorpseOf CorpseOf { get; init; }

    /// <summary>The item's rot stage; empty when it does not rot or the census did not say.</summary>
    public RotStage RotStage { get; init; }

    /// <summary>The holding grave's ID for a buried corpse.</summary>
    public string Grave { get; init; } = "";

    /// <summary>The corpse's vanilla flag: a re-burial fires no memory, so it is never owed a sarcophagus.</summary>
    public bool EverBuriedInSarcophagus { get; init; }
}
