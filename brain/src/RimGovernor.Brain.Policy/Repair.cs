namespace RimGovernor.Brain.Policy;

/// <summary>
/// Mirrors SecureSuppliesHaulerFacts' eligibility inputs, substituting the Construction work-type gate for
/// Hauling: an enabled, non-zero-priority Construction work type and a healthy pawn (no needed tend, no
/// bleeding). EvaluateRepair re-validates the exact chosen pawn/structure pair again immediately before
/// dispatch; this only narrows which already-selected structure and pawn become one Repair proposal.
/// </summary>
public sealed record RepairCandidateFacts
{
    public PawnId Pawn { get; init; }

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Downed { get; init; }

    public Fact<bool> Drafted { get; init; }

    public Fact<bool> MentalState { get; init; }

    public Fact<bool> PlayerForced { get; init; }

    public Fact<bool> NeedsTend { get; init; }

    public Fact<bool> Bleeding { get; init; }

    public Fact<bool> ConstructionEnabled { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Pairs the highest-priority damaged structure (callers pass structures already ordered the way
    /// ReviewUpkeep sorts them: repair priority first, then lowest health fraction, then stable ID) with the
    /// lowest-ID eligible repairer. It is a proposal only; the native preview at dispatch still owns whether
    /// the job is actually accepted.
    /// </summary>
    public static (UpkeepStructure Structure, PawnId Pawn, bool Ok) SelectRepair(
        IReadOnlyList<UpkeepStructure> structures, IReadOnlyList<RepairCandidateFacts> pawns)
    {
        static bool Eligible(RepairCandidateFacts p)
        {
            if (!p.Dead.TryGetValue(out var dead) || !p.Downed.TryGetValue(out var downed)
                || !p.Drafted.TryGetValue(out var drafted) || !p.MentalState.TryGetValue(out var mental)
                || !p.NeedsTend.TryGetValue(out var needsTend) || !p.Bleeding.TryGetValue(out var bleeding)
                || !p.ConstructionEnabled.TryGetValue(out var construction))
            {
                return false;
            }
            return !dead && !downed && !drafted && !mental && !needsTend && !bleeding && construction;
        }

        var pool = pawns.Where(Eligible).ToList();
        if (pool.Count == 0 || structures.Count == 0)
        {
            return (new UpkeepStructure(), default, false);
        }
        var ordered = pool.OrderBy(p => OrderedWorkCost(p.PlayerForced)).ThenBy(p => p.Pawn).ToList();
        return (structures[0], ordered[0].Pawn, true);
    }
}
