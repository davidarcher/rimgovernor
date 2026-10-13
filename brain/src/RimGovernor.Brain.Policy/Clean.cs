namespace RimGovernor.Brain.Policy;

// Go: clean.go

/// <summary>
/// Mirrors SecureSuppliesHaulerFacts' eligibility inputs, substituting a Cleaning work-type gate for Hauling:
/// Cleaning not disabled (priority may be 0: the order is player-forced and the bounded response targets
/// exactly the colonies whose priority coverage failed) and a healthy pawn (no needed tend, no bleeding).
/// EvaluateClean re-validates the exact chosen pawn/filth pair again immediately before dispatch; this only
/// narrows which already-selected filth and pawn become one Clean proposal.
/// </summary>
public sealed record CleanCandidateFacts
{
    public PawnId Pawn { get; init; }

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Downed { get; init; }

    public Fact<bool> Drafted { get; init; }

    public Fact<bool> MentalState { get; init; }

    public Fact<bool> PlayerForced { get; init; }

    public Fact<bool> NeedsTend { get; init; }

    public Fact<bool> Bleeding { get; init; }

    public Fact<bool> CleaningEnabled { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Pairs the highest-priority filth (callers pass filth already ordered the way ReviewUpkeep sorts it:
    /// critical rooms first, then stable ID) with the lowest-ID eligible cleaner. It is a proposal only; the
    /// native preview at dispatch still owns whether the job is actually accepted.
    /// </summary>
    public static (UpkeepFilth Filth, PawnId Pawn)? SelectClean(IReadOnlyList<UpkeepFilth> filth, IEnumerable<CleanCandidateFacts> pawns)
    {
        static bool Eligible(CleanCandidateFacts p)
        {
            var dk = p.Dead.TryGetValue(out var dead);
            var wk = p.Downed.TryGetValue(out var downed);
            var tk = p.Drafted.TryGetValue(out var drafted);
            var mk = p.MentalState.TryGetValue(out var mental);
            var fk = p.PlayerForced.TryGetValue(out var forced);
            var nk = p.NeedsTend.TryGetValue(out var needsTend);
            var bk = p.Bleeding.TryGetValue(out var bleeding);
            var ck = p.CleaningEnabled.TryGetValue(out var cleaning);
            if (!dk || !wk || !tk || !mk || !fk || !nk || !bk || !ck)
            {
                return false;
            }

            return !dead && !downed && !drafted && !mental && !forced && !needsTend && !bleeding && cleaning;
        }

        var pool = pawns.Where(Eligible).ToList();
        if (pool.Count == 0 || filth.Count == 0)
        {
            return null;
        }

        return (filth[0], pool.OrderBy(p => p.Pawn.Value, StringComparer.Ordinal).First().Pawn);
    }
}
