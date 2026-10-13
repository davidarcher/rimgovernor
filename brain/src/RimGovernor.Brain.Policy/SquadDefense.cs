using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Describes one candidate opponent. RangedEquipped decides whether an assigned defender must also be
/// ranged-equipped: a ranged opponent may not be engaged by a melee-only defender.
/// </summary>
public sealed record SquadThreatFacts
{
    public PawnId Id { get; init; }

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Downed { get; init; }

    public Fact<bool> Humanlike { get; init; }

    public Fact<bool> Animal { get; init; }

    public Fact<double> BodySize { get; init; }

    public Fact<bool> Manhunter { get; init; }

    /// <summary>An animal the emergency census lists as hunting a colonist; unknown counts as not hunting.</summary>
    public Fact<bool> Hunting { get; init; }

    public Fact<bool> RangedEquipped { get; init; }

    /// <summary>
    /// Marks a hostile building (an insect hive, a crashed ship part) rather than a pawn, engaged only once no
    /// eligible hostile pawn remains (a hive's insects and a ship part's guards first). Dead is destroyed; the
    /// other pawn facts are irrelevant. A ranged-equipped defender listed under LinesOfFire shoots it from where
    /// it stands (the native ranged predicates need the target in range now); every other defender walks to it
    /// in melee.
    /// </summary>
    public bool Building { get; init; }

    /// <summary>The pawn row's Anomaly facts; unknown without Anomaly.</summary>
    public Fact<PawnAnomaly> Anomaly { get; init; }

    /// <summary>A Mech_ pawn kind, marked from the combat view.</summary>
    public bool Mech { get; init; }

    public IReadOnlyDictionary<PawnId, bool> LinesOfFire { get; init; } = new Dictionary<PawnId, bool>();

    /// <summary>The pawn's MeleeDPS scaled by health.</summary>
    public Fact<double> MeleePower { get; init; }
}

/// <summary>
/// Describes one candidate defender. Health/NeedsTend mirror combat_health_hold: a defender who needs tending
/// or is below the native combat-health threshold is never assigned.
/// </summary>
public record SquadDefenderFacts
{
    public PawnId Id { get; init; }

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Downed { get; init; }

    public Fact<bool> Drafted { get; init; }

    public Fact<bool> MentalState { get; init; }

    public Fact<bool> PlayerForced { get; init; }

    /// <summary>
    /// Whether a live plan needs the pawn drafted. A drafted pawn a plan needs is busy elsewhere; a drafted pawn
    /// no plan needs is a candidate like any undrafted colonist and the draft adopts it.
    /// </summary>
    public Fact<bool> DraftOwned { get; init; }

    /// <summary>A pawn known to be in deathrest: never a defender.</summary>
    public bool Deathresting { get; init; }

    public Fact<uint> QueuedJobs { get; init; }

    public Fact<bool> ViolenceCapable { get; init; }

    public Fact<bool> NeedsTend { get; init; }

    public Fact<double> HealthFraction { get; init; }

    public Fact<bool> RangedEquipped { get; init; }

    public Fact<bool> MeleeEquipped { get; init; }

    public Fact<bool> Armed { get; init; }

    /// <summary>
    /// The pawn's place in FrontLine's split of the roster: a line holder (Tough, Nimble, Brawler or Melee over
    /// Shooting) takes a melee opponent before a shooter does, and shooters take ranged opponents and firing
    /// cells first. False when the profile is unknown; it orders preference only, never eligibility.
    /// </summary>
    public bool FrontLine { get; init; }

    /// <summary>The pawn's worn sharp armor rating; it ranks choke blockers. Unknown ranks after every known rating.</summary>
    public Fact<double> Armor { get; init; }

    /// <summary>The pawn's MeleeDPS scaled by health.</summary>
    public Fact<double> MeleePower { get; init; }

    /// <summary>The primary ranged weapon's damage per second, 0 without one.</summary>
    public Fact<double> RangedDPS { get; init; }

    /// <summary>A pawn with Warden work enabled; a prison break ranks it first.</summary>
    public bool Warden { get; init; }
}

public enum SquadMode : byte
{
    Melee = 1,
    Ranged = 2,
}

public sealed record SquadAssignment(PawnId Defender, PawnId Target, SquadMode Mode);

public static partial class PolicyFunctions
{
    private const int MaxSquadOpponents = 4;
    private const int MaxSquadDefenders = 8;
    private const int RequiredDefendersPerFoe = 2;

    /// <summary>
    /// Whether defenders can win a melee against t: their summed melee power above t's. A gun among them, a
    /// building or any unknown power is not a melee this compares.
    /// </summary>
    private static bool MeleeBeats(SquadThreatFacts t, IEnumerable<SquadDefenderFacts> defenders)
    {
        var ok = t.MeleePower.TryGetValue(out var foe);
        if (t.Building || !ok)
        {
            return true;
        }
        var sum = 0.0;
        foreach (var d in defenders)
        {
            var pk = d.MeleePower.TryGetValue(out var power);
            if (!pk || Positive(d.RangedEquipped))
            {
                return true;
            }
            sum += power;
        }
        return sum > foe;
    }

    /// <summary>
    /// Whether the pawn's draft is spoken for: drafted under an owned claim, or drafted with the claim unknown.
    /// Forced and queued jobs affect candidate preference, never draft ownership.
    /// </summary>
    private static (bool Busy, bool Known) SquadDraftBusy(SquadDefenderFacts d)
    {
        if (!d.Drafted.TryGetValue(out var drafted))
        {
            return (false, false);
        }
        if (!drafted)
        {
            return (false, true);
        }
        var ok = d.DraftOwned.TryGetValue(out var owned);
        return (!ok || owned, true);
    }

    /// <summary>
    /// The shared combat_health_hold gate: known health, not drafted under another claim, violence-capable, not
    /// needing tending and above the native combat-health threshold.
    /// </summary>
    private static bool SquadDefenderEligible(SquadDefenderFacts d) => SquadDefenderReasons(d).Count == 0;

    /// <summary>Every reason d is not eligible, empty when eligible. Each check stands alone: an unknown fact is a reason, and the known ones are still checked.</summary>
    private static List<string> SquadDefenderReasons(SquadDefenderFacts d)
    {
        var why = new List<string>();
        if (!d.Dead.TryGetValue(out var dead))
        {
            why.Add("dead unknown");
        }
        else if (dead)
        {
            why.Add("dead");
        }
        if (!d.Downed.TryGetValue(out var downed))
        {
            why.Add("downed unknown");
        }
        else if (downed)
        {
            why.Add("downed");
        }
        var (busy, bk) = SquadDraftBusy(d);
        if (!bk)
        {
            why.Add("drafted unknown");
        }
        else if (busy)
        {
            why.Add("drafted under another claim");
        }
        if (!d.MentalState.TryGetValue(out var mental))
        {
            why.Add("mental state unknown");
        }
        else if (mental)
        {
            why.Add("in a mental state");
        }
        if (!d.ViolenceCapable.TryGetValue(out var violent))
        {
            why.Add("violence capability unknown");
        }
        else if (!violent)
        {
            why.Add("not violence-capable");
        }
        if (!d.NeedsTend.TryGetValue(out var needsTend))
        {
            why.Add("needs-tend unknown");
        }
        else if (needsTend)
        {
            why.Add("needs tending");
        }
        if (d.Deathresting)
        {
            why.Add("deathresting");
        }
        if (!d.HealthFraction.TryGetValue(out var health))
        {
            why.Add("health unknown");
        }
        else if (health <= SquadHurtHealth)
        {
            why.Add("health " + health.ToString("F2", CultureInfo.InvariantCulture));
        }
        return why;
    }

    private static IEnumerable<SquadDefenderFacts> OrderDefenders(IEnumerable<SquadDefenderFacts> pool) =>
        pool.OrderBy(d => OrderedWorkCost(d.PlayerForced, d.QueuedJobs)).ThenBy(d => d.Id);

    /// <summary>
    /// The bounded core of combat_method.squad_defense: at least two capable defenders per observed opponent,
    /// up to four opponents and eight defenders, with a ranged opponent requiring a ranged-equipped defender.
    /// This proposal covers the general N-opponent case only; the single-raider tribal
    /// 3-defender/85%-health sub-case is the caller's separate SelectTribalRaiderDefense preference, and an
    /// unarmed defender is never sent to melee and is not equipped inline (RoundsEquipPlanner arms colonists on
    /// its own independently-scheduled goal).
    ///
    /// Assignments are a proposal only; native (melee intents) and EvaluateRangedDefense re-validate each
    /// chosen (defender, target) pair against fresh facts before dispatch, and downed/dead opponents drop out
    /// at that point without being re-selected here.
    /// </summary>
    public static (IReadOnlyList<SquadAssignment> Assignments, bool Ok) SelectSquadDefense(
        IReadOnlyList<SquadThreatFacts> threats,
        IReadOnlyList<SquadDefenderFacts> defenders
    )
    {
        static bool EligibleThreat(SquadThreatFacts t)
        {
            var dk = t.Dead.TryGetValue(out var dead);
            if (t.Building)
            {
                return dk && !dead;
            }
            var wk = t.Downed.TryGetValue(out var downed);
            var hk = t.Humanlike.TryGetValue(out var humanlike);
            var ak = t.Animal.TryGetValue(out var animal);
            if (!dk || !wk || dead || downed)
            {
                return false;
            }
            if (t.Mech)
            {
                return true;
            }
            if (!hk || !ak)
            {
                return false;
            }
            if (humanlike || MeleeEntity(t))
            {
                return true;
            }
            if (!animal)
            {
                return false;
            }
            var sk = t.BodySize.TryGetValue(out var size);
            var mk = t.Manhunter.TryGetValue(out var manhunter);
            t.Hunting.TryGetValue(out var hunting);
            if (!sk || !mk || (!manhunter && !hunting))
            {
                return false;
            }
            return size > 0 && size <= 4;
        }

        var threatPool = new List<SquadThreatFacts>();
        var buildings = new List<SquadThreatFacts>();
        var standing = false;
        foreach (var t in threats)
        {
            if (!t.Building)
            {
                var dk = t.Dead.TryGetValue(out var dead);
                var wk = t.Downed.TryGetValue(out var downed);
                standing = standing || !dk || !wk || (!dead && !downed);
            }
            if (!EligibleThreat(t))
            {
            }
            else if (t.Building)
            {
                buildings.Add(t);
            }
            else
            {
                threatPool.Add(t);
            }
        }
        // Buildings wait for the field to clear: a hive's insects and a ship part's guards are the live danger,
        // the building itself goes nowhere. Any standing hostile pawn guards it, eligible or not: a hive is not
        // attacked while its insects still fight.
        if (threatPool.Count == 0 && !standing)
        {
            threatPool = buildings;
        }
        threatPool = [.. threatPool.OrderBy(t => t.Id)];
        if (threatPool.Count > MaxSquadOpponents)
        {
            threatPool = threatPool.GetRange(0, MaxSquadOpponents);
        }
        if (threatPool.Count == 0)
        {
            return ([], false);
        }

        var byId = new Dictionary<PawnId, SquadDefenderFacts>();
        var unsorted = new List<SquadDefenderFacts>();
        foreach (var d in defenders)
        {
            if (SquadDefenderEligible(d))
            {
                unsorted.Add(d);
                byId[d.Id] = d;
            }
        }
        var defenderPool = OrderDefenders(unsorted).ToList();

        // A melee opponent goes to the line holders first and a ranged one to the shooters; either falls back
        // to whoever is left.
        var used = new HashSet<PawnId>();
        (PawnId Id, bool Found) Take(bool ranged)
        {
            foreach (var preferred in new[] { true, false })
            {
                foreach (var d in defenderPool)
                {
                    if (used.Contains(d.Id) || (preferred && d.FrontLine == ranged))
                    {
                        continue;
                    }
                    var known = d.RangedEquipped.TryGetValue(out var equipped);
                    if (ranged && (!known || !equipped))
                    {
                        continue;
                    }
                    // Melee needs a weapon in hand: fists lose to anything worth fighting.
                    var ak = d.Armed.TryGetValue(out var armed);
                    if (!ranged && (!ak || !armed))
                    {
                        continue;
                    }
                    used.Add(d.Id);
                    return (d.Id, true);
                }
            }
            return (default, false);
        }

        // A building takes the shooters with a line of fire on it first, then whoever is left in melee.
        (PawnId Id, SquadMode Mode, bool Found) TakeBuilding(SquadThreatFacts t)
        {
            foreach (var d in defenderPool)
            {
                var known = d.RangedEquipped.TryGetValue(out var equipped);
                if (!used.Contains(d.Id) && known && equipped && t.LinesOfFire.TryGetValue(d.Id, out var line) && line)
                {
                    used.Add(d.Id);
                    return (d.Id, SquadMode.Ranged, true);
                }
            }
            var (id, found) = Take(false);
            return (id, SquadMode.Melee, found);
        }

        List<SquadAssignment> SelectFor(int per)
        {
            var assignments = new List<SquadAssignment>();
            foreach (var t in threatPool)
            {
                if (assignments.Count + per > MaxSquadDefenders)
                {
                    break;
                }
                var known = t.RangedEquipped.TryGetValue(out var ranged);
                if (!t.Building && !known)
                {
                    continue;
                }
                var chosen = new List<SquadAssignment>();
                var ok = true;
                for (var i = 0; i < per; i++)
                {
                    PawnId id;
                    bool found;
                    var mode = SquadMode.Melee;
                    if (t.Building)
                    {
                        (id, mode, found) = TakeBuilding(t);
                    }
                    else
                    {
                        if (ranged)
                        {
                            mode = SquadMode.Ranged;
                        }
                        (id, found) = Take(ranged);
                    }
                    if (!found)
                    {
                        ok = false;
                        break;
                    }
                    chosen.Add(new SquadAssignment(id, t.Id, mode));
                }
                if (ok && !ranged)
                {
                    ok = MeleeBeats(t, chosen.Select(a => byId[a.Defender]));
                }
                if (!ok)
                {
                    // Release any partial reservation; an unsupported encounter is an explicit hold, not a
                    // partially defended one.
                    foreach (var a in chosen)
                    {
                        used.Remove(a.Defender);
                    }
                    continue;
                }
                assignments.AddRange(chosen);
            }
            return assignments;
        }

        var result = SelectFor(RequiredDefendersPerFoe);
        // A colony under twice the raid cannot pair every foe, and defending one foe while the rest hit
        // undefended loses the fight (#2816): with a defender for every foe, each takes one instead.
        var covered = new HashSet<PawnId>(result.Select(a => a.Target));
        if (covered.Count < threatPool.Count && defenderPool.Count >= 2 && defenderPool.Count >= threatPool.Count)
        {
            used.Clear();
            var single = SelectFor(1);
            if (single.Count >= 2 && single.Count > result.Count)
            {
                result = single;
            }
        }
        return (result, result.Count > 0);
    }

    /// <summary>
    /// Mirrors single_raider_defense's tribal branch: a lone humanlike, non-ranged opponent is bounded tighter
    /// than the general N-opponent case above: three healthy (&gt;=85%, not needing tend) already-armed
    /// defenders instead of two. There is no synchronous per-encounter weapon fetch: an unarmed candidate is
    /// simply excluded here rather than equipped inline: EnsureBasicDefense arms colonists on its own
    /// independently-scheduled goal (see RoundsEquipPlanner), so this method just holds (via the caller falling
    /// back to SelectSquadDefense's general, unarmed-tolerant bound) until enough defenders are already armed.
    /// </summary>
    public static (IReadOnlyList<SquadAssignment> Assignments, bool Ok) SelectTribalRaiderDefense(
        SquadThreatFacts threat,
        IReadOnlyList<SquadDefenderFacts> defenders
    )
    {
        if (threat.Building)
        {
            return ([], false);
        }
        var dk = threat.Dead.TryGetValue(out var dead);
        var wk = threat.Downed.TryGetValue(out var downed);
        var hk = threat.Humanlike.TryGetValue(out var humanlike);
        var rk = threat.RangedEquipped.TryGetValue(out var ranged);
        if (!dk || !wk || !hk || !rk || dead || downed || !humanlike || ranged)
        {
            return ([], false);
        }
        static bool Eligible(SquadDefenderFacts d)
        {
            var dk = d.Dead.TryGetValue(out var dead);
            var wk = d.Downed.TryGetValue(out var downed);
            var (drafted, tk) = SquadDraftBusy(d);
            var mk = d.MentalState.TryGetValue(out var mental);
            var vk = d.ViolenceCapable.TryGetValue(out var violent);
            var nk = d.NeedsTend.TryGetValue(out var needsTend);
            var hk2 = d.HealthFraction.TryGetValue(out var health);
            var ak = d.Armed.TryGetValue(out var armed);
            if (!dk || !wk || !tk || !mk || !vk || !nk || !hk2 || !ak)
            {
                return false;
            }
            if (dead || downed || drafted || mental || !violent || needsTend || !armed || d.Deathresting)
            {
                return false;
            }
            return health >= 0.85;
        }
        static bool RangedOf(SquadDefenderFacts d)
        {
            d.RangedEquipped.TryGetValue(out var r);
            return r;
        }
        var pool = defenders
            .Where(Eligible)
            .OrderBy(d => OrderedWorkCost(d.PlayerForced, d.QueuedJobs))
            .ThenByDescending(RangedOf)
            .ThenByDescending(d => d.FrontLine)
            .ThenBy(d => d.Id)
            .ToList();
        if (pool.Count < 3 || !MeleeBeats(threat, pool.GetRange(0, 3)))
        {
            return ([], false);
        }
        var assignments = new List<SquadAssignment>();
        foreach (var d in pool.GetRange(0, 3))
        {
            var mode = RangedOf(d) ? SquadMode.Ranged : SquadMode.Melee;
            assignments.Add(new SquadAssignment(d.Id, threat.Id, mode));
        }
        return (assignments, true);
    }
}
