using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Says whether a colony that never builds or launches a gravship can finish a quest (epic #1707: ground
/// only). The catalog classifies it from the quest's QuestScriptDef row (bridge.QuestClass), never from a
/// def-name list.
/// </summary>
public enum QuestScope
{
    /// <summary>Go's empty string: not classified.</summary>
    [WireName("")]
    None = 0,

    /// <summary>The quest is not defined by Odyssey; the Odyssey selector leaves it to the joiner and Empire selectors.</summary>
    Other,

    /// <summary>An Odyssey quest that names no space planet layer.</summary>
    Ground,

    /// <summary>An Odyssey quest that needs a space planet layer (orbit), which only a ship, shuttle or pod reaches.</summary>
    ShipOnly,
}

/// <summary>One quest root's classification. SpaceLayer names the space planet layer that makes a ship-only quest so.</summary>
public sealed record QuestClass(QuestScope Scope, string SpaceLayer);

/// <summary>
/// Names why a quest offer is not accepted. Go's reasons are an open set of snake_case strings shared by the
/// quest families, so this is a string-valued struct; the default value is Go's empty string (no reason).
/// Every skip is logged by the reviewer, once per quest and reason.
/// </summary>
public readonly struct QuestSkipReason : IEquatable<QuestSkipReason>
{
    private readonly string? _value;

    public QuestSkipReason(string value) => _value = value;

    public string Value => _value ?? "";

    public bool IsNone => Value.Length == 0;

    /// <summary>The quest needs a space layer (QuestClass.SpaceLayer).</summary>
    public static readonly QuestSkipReason ShipOnly = new("ship_only");

    /// <summary>
    /// The catalog could not classify the quest (no row, or a layer reference that names no planet layer
    /// def); unknown stays unknown, so the quest is not accepted.
    /// </summary>
    public static readonly QuestSkipReason ClassUnknown = new("class_unknown");

    /// <summary>A ground quest with several reward choices; no source says which one a ground colony wants.</summary>
    public static readonly QuestSkipReason RewardChoices = new("reward_choices");

    /// <summary>A ground quest that names an accepter pawn; none is chosen here.</summary>
    public static readonly QuestSkipReason NeedsAccepter = new("needs_accepter");

    public static implicit operator QuestSkipReason(string value) => new(value);

    public bool Equals(QuestSkipReason other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is QuestSkipReason other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public static bool operator ==(QuestSkipReason a, QuestSkipReason b) => a.Equals(b);

    public static bool operator !=(QuestSkipReason a, QuestSkipReason b) => !a.Equals(b);

    public override string ToString() => Value;
}

/// <summary>
/// One waiting Odyssey offer that is not accepted and why. Detail is the space layer of a ship-only quest,
/// or why the catalog could not classify an unknown one.
/// </summary>
public sealed record QuestSkip(QuestId Quest, string ScriptDef, QuestSkipReason Reason, string Detail);
