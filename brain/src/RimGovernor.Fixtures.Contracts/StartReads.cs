#nullable enable

namespace RimGovernor.Fixtures.Contracts;

// The read-only fixture replies the start-claim cases read (#3400): what the loaded game holds that no observation
// reports. A record carries only what a case reads; the fixture's other members are ignored.

/// <summary><c>test/quiet_storyteller</c> with action <c>inspect</c>: the loaded game's storyteller and its comp count.</summary>
public sealed record QuietStorytellerRead
{
    /// <summary>The StorytellerDef name.</summary>
    public string Storyteller { get; init; } = "";

    /// <summary>The storyteller's live storytellerComps.</summary>
    public int Comps { get; init; }
}

/// <summary><c>test/ideoligion_design_inspect</c>: the player's primary ideoligion design and reform progression.</summary>
public sealed record IdeoligionDesignRead
{
    /// <summary>The design as the wire's IdeoligionDesign JSON.</summary>
    public string Design { get; init; } = "";

    /// <summary>The reforms the ideoligion has made.</summary>
    public int Count { get; init; }

    /// <summary>The development points it holds.</summary>
    public int Points { get; init; }
}
