using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

/// <summary>
/// The store-haul family (#3624): the hauls standby colonists could make, each a one-trip row native read for a loose
/// stack to the storage it chose. It binds as the planned-clearing haul does (#3614): one Deliver step of the whole stack
/// in one carry. Valuing and choosing among them is <see cref="StoreHaulRules"/>'s.
/// </summary>
internal static class StoreHaulCandidates
{
    /// <summary>
    /// Every one-trip row of <paramref name="pawns"/> with a known delivery, for a stack not <paramref name="reserved"/>
    /// (claimed, or a target pending services name) and not on a <paramref name="reservedCells"/> cell (a pending supply's
    /// or cook's source).
    /// </summary>
    internal static IEnumerable<(StoreHaul Haul, ConstructionActivityOption Row)> Offers(StoreHaulRead read, IReadOnlyCollection<string> pawns,
        IReadOnlySet<string> reserved, IReadOnlySet<Cell> reservedCells) =>
        read.Hauls
            .Where(h => !reserved.Contains(h.Thing) && !reservedCells.Contains(h.Origin))
            .SelectMany(h => h.Options.Where(r => pawns.Contains(r.Pawn) && ConstructionMethodCandidates.OneTrip(r) && r.DeliveryTicks.IsKnown)
                .Select(r => (h, r)));

    /// <summary>The haul's task: the Deliver of the stack to its storage cell, until <paramref name="deadline"/>.</summary>
    internal static ActivityTaskCandidate Task(StoreHaul haul, ConstructionActivityOption row, long deadline) =>
        ConstructionMethodCandidates.RemovalBinding("haul/" + haul.Thing, PlannedClearingKind.Haul, row, haul.Thing, deadline);
}
