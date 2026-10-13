namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Routes the same OccupantCapture decision the journal records. Ordinary visitors, hostile ancients and
    /// downed capture candidates cannot enter this standing-neutral path. Unknown capacity authorizes nobody
    /// (the empty id).
    /// </summary>
    public static RimGovernor.Brain.Domain.PawnId ShrineArrestTarget(RoundsFacts f)
    {
        if (!JoinerCapacity(f.JoinerCapacity()).TryGetValue(out var room) || !room)
        {
            return default;
        }
        if (!f.Upkeep.Shrines.TryGetValue(out var shrines))
        {
            return default;
        }
        RimGovernor.Brain.Domain.PawnId target = default;
        foreach (var shrine in shrines)
        {
            foreach (var occupant in shrine.Occupants)
            {
                if (occupant.Faction != "Ancients" || occupant.Downed || occupant.Hostile || OccupantDecision(occupant, room) != OccupantCapture)
                {
                    continue;
                }
                var pawn = new RimGovernor.Brain.Domain.PawnId(occupant.EntityId);
                if (target.IsEmpty || pawn < target)
                {
                    target = pawn;
                }
            }
        }
        return target;
    }

    /// <summary>Selects an unoccupied, unowned humanlike prisoner bed. Native Arrest rechecks suitability, reach and reservations before ordering.</summary>
    public static string ShrineArrestBed(Fact<SleepingObservation> sleeping)
    {
        if (!sleeping.TryGetValue(out var census))
        {
            return "";
        }
        var bed = "";
        foreach (var candidate in census.Beds)
        {
            var hk = candidate.Humanlike.TryGetValue(out var human);
            var pk = candidate.Prisoners.TryGetValue(out var prisoner);
            if (!hk || !pk || !human || !prisoner || candidate.Users.Count != 0 || candidate.Owners.Count != 0)
            {
                continue;
            }
            if (bed == "" || string.CompareOrdinal(candidate.Id, bed) < 0)
            {
                bed = candidate.Id;
            }
        }
        return bed;
    }

    public static RimGovernor.Brain.Domain.PawnId ShrineArrester(IReadOnlyList<ShrineDefenderFacts> squad)
    {
        RimGovernor.Brain.Domain.PawnId pawn = default;
        foreach (var candidate in squad)
        {
            if (ShrineDefenderEligible(candidate) && (pawn.IsEmpty || candidate.Id.CompareTo((PawnId)pawn) < 0))
            {
                pawn = (Domain.PawnId)candidate.Id;
            }
        }
        return pawn;
    }
}
