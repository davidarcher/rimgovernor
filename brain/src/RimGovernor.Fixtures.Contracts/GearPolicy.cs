#nullable enable

namespace RimGovernor.Fixtures.Contracts;

/// <summary>
/// The reply of <c>test/gear_fixture</c>'s player-outfit modes: <c>policy_setup</c> puts the first able colonist on a
/// new player outfit wearing one forced, locked shirt, and <c>policy_edit</c> allows tainted apparel on that outfit.
/// The primitives cannot make a player outfit or force and lock apparel.
/// </summary>
public sealed record GearPolicyReply
{
    /// <summary>Whether the fixture ran.</summary>
    public bool Success { get; init; }

    /// <summary>The subject pawn's load id.</summary>
    public string Pawn { get; init; } = "";

    /// <summary>The subject's short name, the label its own outfit takes.</summary>
    public string ShortName { get; init; } = "";
}

/// <summary>
/// The reply of <c>test/gear_fixture</c>'s <c>prune_setup</c> mode: the first able colonist on a new extra outfit and a
/// new extra allowed area. The primitives cannot make a player outfit or an allowed area.
/// </summary>
public sealed record GearPruneReply
{
    /// <summary>Whether the fixture ran.</summary>
    public bool Success { get; init; }

    /// <summary>The subject pawn's load id.</summary>
    public string Pawn { get; init; } = "";

    /// <summary>The subject's short name, the label its own outfit takes.</summary>
    public string ShortName { get; init; } = "";

    /// <summary>The extra outfit's load id.</summary>
    public string Outfit { get; init; } = "";

    /// <summary>The extra allowed area's load id.</summary>
    public string Area { get; init; } = "";
}
