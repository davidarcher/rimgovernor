namespace RimGovernor.Brain.Policy;


/// <summary>One native corpse census row: the corpse's inner pawn class and, once buried, its grave.</summary>
public sealed record WasteItem
{
    /// <summary>The corpse's inner pawn class.</summary>
    public CorpseOf CorpseOf { get; init; }

    /// <summary>The holding grave's ID for a buried corpse; empty while unburied.</summary>
    public string Grave { get; init; } = "";

    /// <summary>Whether a grave holds the corpse.</summary>
    public bool Buried => Grave != "";
}
