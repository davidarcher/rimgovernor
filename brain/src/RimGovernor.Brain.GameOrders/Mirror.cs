using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Mirror;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameOrders.Mirror;

/// <summary>
/// rimgovernor/combat_geometry: an on-demand read, not mirrored, that answers DecideCombat's geometry questions at a
/// stop by the game's own rules. Caps: at most MaxGeometryCells cells and MaxGeometryHostiles hostiles (the native's
/// CombatGeometry constants, bridge.CombatGeometry*), measured on lab-ranged under the 50 ms main-thread budget. A
/// request the native cannot answer (a bad shape, a stale identity, a hostile or pawn not spawned, a cell out of
/// bounds) is refused; the reply is always the whole geometry.
/// </summary>
public sealed record CombatGeometryRequest
{
    public required RimGovernor.Brain.GameFacts.Common.Identity Identity { get; init; }

    /// <summary>
    /// 1..cap distinct, in-bounds cells; 0..cap-1 with a propose block (named plus proposed cells together score at
    /// most the cells cap).
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.Domain.Cell>))]
    public required EquatableArray<RimGovernor.Brain.Domain.Cell> Cells { get; init; }

    /// <summary>1..cap distinct pawn load ids; 0..cap for named cells alone (their standability), with no propose.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PawnId>))]
    public required EquatableArray<PawnId> HostileIds { get; init; }

    /// <summary>The role the native proposes candidate cells for, scored like named cells in the same read; none for named cells alone.</summary>
    public CombatGeometryPropose? Propose { get; init; }
}
