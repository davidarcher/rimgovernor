using DomainPawnId = RimGovernor.Brain.Domain.PawnId;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The review's casket stance: Opening maps a shrine to CasketOpen when ShrineOpenReadiness held at the
/// review, or to the readiness hold reason. A shrine it does not name, the zero value, leaves every filled
/// casket sealed; the goal then finishes with the caskets sealed and re-arms the next review the gate holds.
/// </summary>
public sealed record ShrinePolicy
{
    public IReadOnlyDictionary<string, string> Opening { get; init; } = new Dictionary<string, string>();
}

/// <summary>One humanlike the caskets released, or its corpse, as the census reads it. Megascarabs are never listed: they are never hostile and the colony ignores them.</summary>
public sealed record ShrineOccupant
{
    public string EntityId { get; init; } = "";

    public bool Hostile { get; init; }

    public bool Downed { get; init; }

    public bool Dead { get; init; }

    public bool Prisoner { get; init; }

    public string Faction { get; init; } = "";
}

/// <summary>
/// The melee lock's staffing: one violence-capable melee colonist per filled casket (Lockers, by casket
/// identity), the casket the Opener opens (opening one casket opens every casket of the group), or the hold
/// reason when the squad cannot cover every casket.
/// </summary>
public sealed record ShrineLock
{
    public IReadOnlyDictionary<string, DomainPawnId> Lockers { get; init; } = new Dictionary<string, DomainPawnId>();

    public DomainPawnId Opener { get; init; }

    public string Casket { get; init; } = "";

    public string Reason { get; init; } = "";
}

/// <summary>
/// What ShrineOpenReadiness judges one shrine's filled caskets from. CustodyRoom is JoinerCapacity's reading;
/// Medicine the usable medicine count; Doctors the colonists capable of Doctor work.
/// </summary>
public sealed record ShrineOpenRequest
{
    public EquatableArray<ShrineCasket> Caskets { get; init; }

    public EquatableArray<ShrineDefenderFacts> Squad { get; init; }

    public Fact<bool> CustodyRoom { get; init; }

    public Fact<long> Medicine { get; init; }

    public Fact<int> Doctors { get; init; }

    public bool Emergency { get; init; }

    public bool Combat { get; init; }

    public Fact<double> RaidPoints { get; init; }
}

public static partial class PolicyFunctions
{
    // Casket decisions and holds under the opening policy.

    /// <summary>A filled casket the policy opens.</summary>
    public const string CasketOpen = "open";

    /// <summary>Holds a shrine whose filled caskets outnumber the violence-capable melee colonists free to stand in front of them.</summary>
    public const string CasketHoldLockUnderstaffed = "lock_understaffed";

    // Occupant decisions: what the colony does with each humanlike the caskets released.
    public const string OccupantCapture = "capture";
    public const string OccupantRelease = "release";
    public const string OccupantBury = "bury";
    public const string OccupantFight = "fight";
    public const string OccupantCaptured = "captured";

    /// <summary>
    /// Decides one released occupant. A corpse is buried (MaintainBurial's work); a standing hostile is fought
    /// (ActiveCombat's); a downed hostile and a standing neutral are captured while custody has room, released
    /// otherwise; a colony prisoner is done. custodyRoom is JoinerCapacity's reading: whether the colony can
    /// host one more.
    /// </summary>
    public static string OccupantDecision(ShrineOccupant occupant, bool custodyRoom)
    {
        if (occupant.Dead)
        {
            return OccupantBury;
        }
        if (occupant.Prisoner)
        {
            return OccupantCaptured;
        }
        if (occupant.Hostile && !occupant.Downed)
        {
            return OccupantFight;
        }
        if (custodyRoom)
        {
            return OccupantCapture;
        }
        return OccupantRelease;
    }

    /// <summary>The filled caskets the goal owes an opening on: those of an open, guard-free shrine touching Home, in casket identity order per shrine, and only under a policy that opens caskets.</summary>
    public static Dictionary<string, List<ShrineCasket>> ShrineOpenTargets(IReadOnlyList<AncientShrine> rows, ShrinePolicy shrine)
    {
        var output = new Dictionary<string, List<ShrineCasket>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!row.InHome || row.Sealed || !row.GuardsKnown || row.GuardsAlive())
            {
                continue;
            }
            var caskets = row.Caskets.Where(c => CasketDecisionUnder(c, row, shrine) == CasketOpen)
                .OrderBy(c => c.EntityId, StringComparer.Ordinal)
                .ToList();
            if (caskets.Count == 0)
            {
                continue;
            }
            output[row.Id] = caskets;
        }
        return output;
    }

    /// <summary>
    /// Staffs the lock. Eligible colonists are the squad defenders SelectSquadDefense would draft whose
    /// primary is not a ranged weapon (a melee weapon or fists: MeleeEquipped is equipment known); the
    /// healthiest stand first, the opener is the locker of the lowest casket. One colonist is never left free
    /// here: the opener is a locker, and the ancients wake with cryptosleep sickness, so the fight is the lock
    /// itself.
    /// </summary>
    public static ShrineLock ShrineMeleeLock(IReadOnlyList<ShrineCasket> caskets, IReadOnlyList<ShrineDefenderFacts> squad)
    {
        if (caskets.Count == 0)
        {
            return new ShrineLock { Reason = CasketHoldLockUnderstaffed };
        }
        var pool = new List<ShrineDefenderFacts>();
        foreach (var d in squad)
        {
            var ok = d.MeleeEquipped.TryGetValue(out var melee);
            var rk = d.RangedEquipped.TryGetValue(out var ranged);
            if (ShrineDefenderEligible(d) && ok && melee && rk && !ranged)
            {
                pool.Add(d);
            }
        }
        if (pool.Count < caskets.Count)
        {
            return new ShrineLock { Reason = CasketHoldLockUnderstaffed };
        }
        pool = pool
            .OrderByDescending(d => d.HealthFraction.TryGetValue(out var h) ? h : 0)
            .ThenBy(d => d.Id)
            .ToList();
        var sorted = caskets.OrderBy(c => c.EntityId, StringComparer.Ordinal).ToList();
        var lockers = new Dictionary<string, DomainPawnId>(StringComparer.Ordinal);
        for (var i = 0; i < sorted.Count; i++)
        {
            lockers[sorted[i].EntityId] = (Domain.PawnId)pool[i].Id;
        }
        return new ShrineLock { Lockers = lockers, Opener = (Domain.PawnId)pool[0].Id, Casket = sorted[0].EntityId };
    }

    // Opening readiness holds: each names the first unmet condition of ShrineOpenReadiness; the caskets stay
    // sealed and the goal re-arms once the gate holds.
    public const string CasketHoldNoBackup = "open_no_ranged_backup";
    public const string CasketHoldLockInjured = "open_lock_injured";
    public const string CasketHoldNoCustody = "open_no_prisoner_bed";
    public const string CasketHoldNoMedicine = "open_no_medicine";
    public const string CasketHoldNoDoctor = "open_no_doctor";
    public const string CasketHoldEmergency = "open_emergency";
    public const string CasketHoldCombat = "open_combat_active";
    public const string CasketHoldThreatUnknown = "open_threat_unknown";
    public const string CasketHoldThreatTooHigh = "open_threat_too_high";

    internal const double ShrineLockHealthMinimum = 0.8;

    /// <summary>
    /// Permits CasketOpen only when a melee lock of healthy colonists covers every filled casket with one
    /// armed ranged colonist besides, custody has a bed for a captive, there is a medicine per casket and a
    /// doctor, nothing is on fire, and raid points sit under the breach ceiling for a squad of that size.
    /// Otherwise the first unmet condition's hold reason.
    /// </summary>
    public static string ShrineOpenReadiness(ShrineOpenRequest r)
    {
        var lck = ShrineMeleeLock(r.Caskets, r.Squad);
        if (lck.Reason != "")
        {
            return lck.Reason;
        }
        var lockers = new HashSet<DomainPawnId>(lck.Lockers.Values);
        var backup = false;
        foreach (var d in r.Squad)
        {
            if (lockers.Contains((Domain.PawnId)d.Id))
            {
                if (!d.HealthFraction.TryGetValue(out var health) || health < ShrineLockHealthMinimum)
                {
                    return CasketHoldLockInjured;
                }
                continue;
            }
            backup = backup || (ShrineDefenderEligible(d) && ShrineRanged(d));
        }
        if (!backup)
        {
            return CasketHoldNoBackup;
        }
        if (!r.CustodyRoom.TryGetValue(out var room) || !room)
        {
            return CasketHoldNoCustody;
        }
        if (!r.Medicine.TryGetValue(out var medicine) || medicine < r.Caskets.Count)
        {
            return CasketHoldNoMedicine;
        }
        if (!r.Doctors.TryGetValue(out var doctors) || doctors == 0)
        {
            return CasketHoldNoDoctor;
        }
        if (r.Emergency)
        {
            return CasketHoldEmergency;
        }
        if (r.Combat)
        {
            return CasketHoldCombat;
        }
        if (!r.RaidPoints.TryGetValue(out var points) || double.IsNaN(points) || double.IsInfinity(points))
        {
            return CasketHoldThreatUnknown;
        }
        if (points > ShrineSquadCeiling(lck.Lockers.Count + 1))
        {
            return CasketHoldThreatTooHigh;
        }
        return CasketOpen;
    }
}
