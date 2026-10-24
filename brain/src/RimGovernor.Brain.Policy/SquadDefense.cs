using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Describes one candidate opponent. RangedEquipped decides whether an assigned defender must also be
/// ranged-equipped: a ranged opponent may not be engaged by a melee-only defender.
/// </summary>
public sealed record SquadThreatFacts
{
    public PawnId Id { get; init; }

    public bool Dead { get; init; }

    public bool Downed { get; init; }

    public bool Humanlike { get; init; }

    public bool Animal { get; init; }

    /// <summary>The race's base body size (RaceProperties.baseBodySize); zero for a hostile building.</summary>
    public double BodySize { get; init; }

    public bool Manhunter { get; init; }

    /// <summary>An animal the emergency census lists as hunting a colonist.</summary>
    public bool Hunting { get; init; }

    public bool RangedEquipped { get; init; }

    /// <summary>
    /// Marks a hostile building (an insect hive, a crashed ship part) rather than a pawn, engaged only once no
    /// eligible hostile pawn remains (a hive's insects and a ship part's guards first). Dead is destroyed; the
    /// other pawn facts are irrelevant. A ranged-equipped defender listed under LinesOfFire shoots it from where
    /// it stands (the native ranged predicates need the target in range now); every other defender walks to it
    /// in melee.
    /// </summary>
    public bool Building { get; init; }

    /// <summary>The pawn row's Anomaly facts, <see cref="PawnAnomalyState.Inactive"/> without Anomaly and for a hostile building.</summary>
    public required PawnAnomalyState Anomaly { get; init; }

    /// <summary>A Mech_ pawn kind, marked from the combat view.</summary>
    public bool Mech { get; init; }

    public IReadOnlyDictionary<PawnId, bool> LinesOfFire { get; init; } = new Dictionary<PawnId, bool>();

    /// <summary>The pawn's MeleeDPS scaled by health; zero for a hostile building.</summary>
    public double MeleePower { get; init; }
}

/// <summary>
/// A defender's worn sharp armor: <see cref="Rated"/> from its combat mirror row, or <see cref="Unmirrored"/> while the
/// combat mirror lists no row for it (no hostile stands: a hunt, or a break the combat read is not open for).
/// </summary>
public abstract record DefenderArmor
{
    private DefenderArmor() { }

    /// <summary>The stronger of natural armor and the best worn layer.</summary>
    public sealed record Rated(double Sharp) : DefenderArmor;

    public sealed record Unmirrored : DefenderArmor;
}

/// <summary>
/// Describes one candidate defender. Health/NeedsTend mirror combat_health_hold: a defender who needs tending
/// or is below the native combat-health threshold is never assigned.
/// </summary>
public record SquadDefenderFacts
{
    public PawnId Id { get; init; }

    public bool Dead { get; init; }

    public bool Downed { get; init; }

    public bool Drafted { get; init; }

    public bool MentalState { get; init; }

    public bool PlayerForced { get; init; }

    /// <summary>
    /// Whether a live plan needs the pawn drafted. A drafted pawn a plan needs is busy elsewhere; a drafted pawn
    /// no plan needs is a candidate like any undrafted colonist and the draft adopts it.
    /// </summary>
    public bool DraftOwned { get; init; }

    /// <summary>A pawn in deathrest: never a defender.</summary>
    public bool Deathresting { get; init; }

    public uint QueuedJobs { get; init; }

    public bool ViolenceCapable { get; init; }

    public bool NeedsTend { get; init; }

    public double HealthFraction { get; init; }

    public bool RangedEquipped { get; init; }

    /// <summary>The pawn's primary is a melee weapon.</summary>
    public bool MeleeEquipped { get; init; }

    public bool Armed { get; init; }

    /// <summary>
    /// The pawn's place in FrontLine's split of the roster: a line holder (Tough, Nimble, Brawler or Melee over
    /// Shooting) takes a melee opponent before a shooter does, and shooters take ranged opponents and firing
    /// cells first. It orders preference only, never eligibility.
    /// </summary>
    public bool FrontLine { get; init; }

    /// <summary>The pawn's worn sharp armor rating; it ranks choke blockers, an unmirrored one after every rated one.</summary>
    public required DefenderArmor Armor { get; init; }

    /// <summary>The pawn's MeleeDPS scaled by health.</summary>
    public double MeleePower { get; init; }

    /// <summary>A pawn with Warden work enabled; a prison break ranks it first.</summary>
    public bool Warden { get; init; }
}

public enum SquadMode : byte
{
    Melee = 1,
    Ranged = 2,
}

public sealed record SquadAssignment(PawnId Defender, PawnId Target, SquadMode Mode);

public static partial class SquadDefenseRules
{
    private const int MaxSquadOpponents = 4;
    internal const int MaxSquadDefenders = 8;
    private const int RequiredDefendersPerFoe = 2;

    /// <summary>
    /// Whether defenders can win a melee against t: their summed melee power above t's. A gun among them or a
    /// building is not a melee this compares.
    /// </summary>
    private static bool MeleeBeats(SquadThreatFacts t, IEnumerable<SquadDefenderFacts> defenders)
    {
        if (t.Building)
        {
            return true;
        }
        var sum = 0.0;
        foreach (var d in defenders)
        {
            if (d.RangedEquipped)
            {
                return true;
            }
            sum += d.MeleePower;
        }
        return sum > t.MeleePower;
    }

    /// <summary>
    /// Whether the pawn's draft is spoken for: drafted under an owned claim.
    /// Forced and queued jobs affect candidate preference, never draft ownership.
    /// </summary>
    internal static bool SquadDraftBusy(SquadDefenderFacts d) => d.Drafted && d.DraftOwned;

    /// <summary>
    /// The shared combat_health_hold gate: not drafted under another claim, violence-capable, not
    /// needing tending and above the native combat-health threshold.
    /// </summary>
    internal static bool SquadDefenderEligible(SquadDefenderFacts d) => SquadDefenderReasons(d).Count == 0;

    /// <summary>Every reason d is not eligible, empty when eligible. Each check stands alone.</summary>
    internal static List<string> SquadDefenderReasons(SquadDefenderFacts d)
    {
        var why = new List<string>();
        if (d.Dead)
        {
            why.Add("dead");
        }
        if (d.Downed)
        {
            why.Add("downed");
        }
        if (SquadDraftBusy(d))
        {
            why.Add("drafted under another claim");
        }
        if (d.MentalState)
        {
            why.Add("in a mental state");
        }
        if (!d.ViolenceCapable)
        {
            why.Add("not violence-capable");
        }
        if (d.NeedsTend)
        {
            why.Add("needs tending");
        }
        if (d.Deathresting)
        {
            why.Add("deathresting");
        }
        if (d.HealthFraction <= CombatSquadRosterRules.SquadHurtHealth)
        {
            why.Add("health " + d.HealthFraction.ToString("F2", CultureInfo.InvariantCulture));
        }
        return why;
    }

    private static IEnumerable<SquadDefenderFacts> OrderDefenders(IEnumerable<SquadDefenderFacts> pool) =>
        pool.OrderBy(d => OrderedWorkRules.OrderedWorkCost(d.PlayerForced, d.QueuedJobs)).ThenBy(d => d.Id);

    /// <summary>
    /// The bounded core of combat_method.squad_defense: at least two capable defenders per observed opponent,
    /// up to four opponents and eight defenders, with a ranged opponent requiring a ranged-equipped defender.
    /// This proposal covers the general N-opponent case only; the single-raider tribal
    /// 3-defender/85%-health sub-case is the caller's separate SelectTribalRaiderDefense preference, and an
    /// unarmed defender is never sent to melee and is not equipped inline (arming is the
    /// military/defense Need's).
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
            if (t.Building)
            {
                return !t.Dead;
            }
            if (t.Dead || t.Downed)
            {
                return false;
            }
            if (t.Mech || t.Humanlike || CombatAnomalyRules.MeleeEntity(t))
            {
                return true;
            }
            if (!t.Animal || (!t.Manhunter && !t.Hunting))
            {
                return false;
            }
            return t.BodySize > 0 && t.BodySize <= 4;
        }

        var threatPool = new List<SquadThreatFacts>();
        var buildings = new List<SquadThreatFacts>();
        var standing = false;
        foreach (var t in threats)
        {
            if (!t.Building)
            {
                standing = standing || (!t.Dead && !t.Downed);
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
                    if (ranged && !d.RangedEquipped)
                    {
                        continue;
                    }
                    // Melee needs a weapon in hand: fists lose to anything worth fighting.
                    if (!ranged && !d.Armed)
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
                if (!used.Contains(d.Id) && d.RangedEquipped && t.LinesOfFire.TryGetValue(d.Id, out var line) && line)
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
                var ranged = t.RangedEquipped;
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
    /// simply excluded here rather than equipped inline: arming is the military/defense Need's
    /// (see MilitaryFamily), so this method just holds (via the caller falling
    /// back to SelectSquadDefense's general, unarmed-tolerant bound) until enough defenders are already armed.
    /// </summary>
    public static (IReadOnlyList<SquadAssignment> Assignments, bool Ok) SelectTribalRaiderDefense(
        SquadThreatFacts threat,
        IReadOnlyList<SquadDefenderFacts> defenders
    )
    {
        if (threat.Building || threat.Dead || threat.Downed || !threat.Humanlike || threat.RangedEquipped)
        {
            return ([], false);
        }
        static bool Eligible(SquadDefenderFacts d) =>
            !d.Dead && !d.Downed && !SquadDraftBusy(d) && !d.MentalState && d.ViolenceCapable && !d.NeedsTend && d.Armed && !d.Deathresting && d.HealthFraction >= 0.85;
        var pool = defenders
            .Where(Eligible)
            .OrderBy(d => OrderedWorkRules.OrderedWorkCost(d.PlayerForced, d.QueuedJobs))
            .ThenByDescending(d => d.RangedEquipped)
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
            var mode = d.RangedEquipped ? SquadMode.Ranged : SquadMode.Melee;
            assignments.Add(new SquadAssignment(d.Id, threat.Id, mode));
        }
        return (assignments, true);
    }
}
