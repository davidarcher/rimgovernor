namespace RimGovernor.Brain.Policy;

/// <summary>
/// A verb's default projectile class (Catalog.HuntWeapon): Bullet is exactly the game's Bullet class (arrows
/// included), Other any other class; None is no projectile.
/// </summary>
public enum HuntProjectile
{
    [WireName("")]
    None = 0,

    [WireName("bullet")]
    Bullet,

    [WireName("other")]
    Other,
}

/// <summary>One verb of a hunter's primary weapon, raw.</summary>
public sealed record HuntVerb
{
    public bool Melee { get; init; }
    public bool AIWeapon { get; init; }
    public double Range { get; init; }
    public double ExplosionRadius { get; init; }
    public double Warmup { get; init; }
    public HuntProjectile Projectile { get; init; }
    public string DamageDef { get; init; } = "";
    public string DamageWorker { get; init; } = "";
}

/// <summary>A hunter's primary weapon, raw; a hunter without one is unarmed.</summary>
public sealed record HuntWeapon
{
    public string DefName { get; init; } = "";
    public bool Ranged { get; init; }
    public bool Melee { get; init; }
    public IReadOnlyList<HuntVerb> Verbs { get; init; } = [];

    /// <summary>
    /// Reports the vanilla hunting weapon: ranged, with at least one attack verb and every attack verb a damaging
    /// projectile (bullet or arrow, no blast radius, no flame damage).
    /// </summary>
    public bool Hunts()
    {
        if (!Ranged)
        {
            return false;
        }
        var n = 0;
        foreach (var v in Verbs)
        {
            if (v.Melee || !v.AIWeapon)
            {
                continue;
            }
            n++;
            if (v.Projectile != HuntProjectile.Bullet || v.ExplosionRadius != 0 || v.DamageDef.Length == 0 || v.DamageWorker == PolicyFunctions.FlameDamageWorker)
            {
                return false;
            }
        }
        return n > 0;
    }
}

/// <summary>
/// One free colonist's raw hunting facts. HuntingPriority is the work priority (0 is off), HuntingActive the
/// game's WorkIsActive. HasHuntingWeapon and RangedBlockingShield mirror the game's own hunting-weapon and shield
/// checks. The Route*Prey sets carry native's per-row route verdict; ReachableBenches the butcher benches the
/// colonist can reach.
/// </summary>
public sealed record HuntHunter
{
    public string ID { get; init; } = "";
    public Cell Cell { get; init; }
    public bool Downed { get; init; }
    public bool MentalState { get; init; }
    public bool Drafted { get; init; }
    public int HuntingPriority { get; init; }
    public bool HuntingActive { get; init; }
    public bool HuntingDisabled { get; init; }
    public bool CookingActive { get; init; }
    public HuntWeapon? Weapon { get; init; }
    public bool HasHuntingWeapon { get; init; }
    public bool RangedBlockingShield { get; init; }
    public IReadOnlySet<string> ReachableBenches { get; init; } = new HashSet<string>();

    /// <summary>Evaluated route answer: the row is safe.</summary>
    public IReadOnlySet<string> RouteSafePrey { get; init; } = new HashSet<string>();

    /// <summary>Evaluated route answer: the row is unsafe.</summary>
    public IReadOnlySet<string> RouteUnsafePrey { get; init; } = new HashSet<string>();

    /// <summary>The native budget ran out before this row. A row in none of the three sets was not evaluated.</summary>
    public IReadOnlySet<string> RouteSkippedPrey { get; init; } = new HashSet<string>();
}

/// <summary>A bill's repeat mode.</summary>
public enum HuntRepeat
{
    [WireName("")]
    Unset = 0,

    [WireName("forever")]
    Forever,

    [WireName("count")]
    Count,

    [WireName("target")]
    Target,
}

/// <summary>A butcher-flesh bill on a bench, raw. Product is the bill's product count, known for a target-count bill only.</summary>
public sealed record HuntBill
{
    public bool Suspended { get; init; }
    public bool Paused { get; init; }
    public HuntRepeat Repeat { get; init; }
    public int Count { get; init; }
    public int Target { get; init; }
    public int? Product { get; init; }
    public IReadOnlySet<string> AllowedCorpses { get; init; } = new HashSet<string>();

    internal bool Running(string corpse)
    {
        if (Suspended || Paused || !AllowedCorpses.Contains(corpse))
        {
            return false;
        }
        return Repeat switch
        {
            HuntRepeat.Forever => true,
            HuntRepeat.Count => Count > 0,
            HuntRepeat.Target => Product is { } product && product < Target,
            _ => false,
        };
    }
}

/// <summary>A bench carrying butcher-flesh bills.</summary>
public sealed record HuntBench
{
    public string ID { get; init; } = "";
    public bool Usable { get; init; }
    public IReadOnlyList<HuntBill> Bills { get; init; } = [];
}

/// <summary>A hunt row policy offers no hunt for: the first gate it fails, and for no_hunter each colonist's reason ("&lt;id&gt; &lt;reason&gt;").</summary>
public sealed record HuntHold
{
    public string ID { get; init; } = "";
    public string Reason { get; init; } = "";
    public IReadOnlyList<string> Detail { get; init; } = [];

    /// <summary>The held prey, set by the census decoder, so the plan can price the prerequisite its hold waits on (HuntPrerequisiteCandidates).</summary>
    public AcquisitionSource Source { get; init; } = new();
}

/// <summary>A hunt row with the raw prey flags that are not part of the source: fogged and in a mental state.</summary>
public sealed record HuntPrey
{
    public AcquisitionSource Source { get; init; } = new();
    public bool Fogged { get; init; }
    public bool Mental { get; init; }

    /// <summary>The pest rule: a pest race that is not in a mental state.</summary>
    internal bool IsPest() => Source.Pest && !Mental;
}

/// <summary>The gate's answer for one hunt row. A null Hold offers the row, with WeaponRange the longest reach among the colonists who can hunt it with a ranged weapon.</summary>
public sealed record HuntVerdict
{
    public HuntHold? Hold { get; init; }
    public double WeaponRange { get; init; }
}

/// <summary>Native's raw hunt facts: every free colonist and every butcher bench.</summary>
public sealed record HuntCensus
{
    public IReadOnlyList<HuntHunter> Hunters { get; init; } = [];
    public IReadOnlyList<HuntBench> Benches { get; init; } = [];

    /// <summary>Reports a usable bench with a running butcher bill that accepts the corpse and a Cooking worker (standing, active, able to reach the bench).</summary>
    public bool ButcherReady(string corpse)
    {
        foreach (var bench in Benches)
        {
            if (!bench.Usable)
            {
                continue;
            }
            foreach (var bill in bench.Bills)
            {
                if (!bill.Running(corpse))
                {
                    continue;
                }
                foreach (var h in Hunters)
                {
                    if (!h.Downed && !h.MentalState && h.CookingActive && h.ReachableBenches.Contains(bench.ID))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>Why a colonist cannot hunt the prey, empty when they can; order is the gate's: downed, mental state, Hunting off, weapon (a shield blocks a ranged one), reach, route.</summary>
    private string HunterReason(HuntHunter h, HuntPrey prey)
    {
        var meleeable = prey.Source.MeleeOnly;
        var shooter = h.Weapon is not null && h.Weapon.Hunts();
        var armed = (shooter && !h.RangedBlockingShield) || (meleeable && (h.Weapon is null || h.Weapon.Melee));
        long dx = h.Cell.X - prey.Source.Cell.X;
        long dz = h.Cell.Z - prey.Source.Cell.Z;
        if (h.Downed)
        {
            return PolicyFunctions.HuntHunterDowned;
        }
        if (h.MentalState)
        {
            return PolicyFunctions.HuntHunterMental;
        }
        if (!h.HuntingActive)
        {
            return PolicyFunctions.HuntHunterInactive;
        }
        if (!armed && shooter)
        {
            return PolicyFunctions.HuntHunterShield;
        }
        if (!armed)
        {
            return PolicyFunctions.HuntHunterNoWeapon;
        }
        if (!prey.IsPest() && dx * dx + dz * dz > PolicyFunctions.HuntReachSquared)
        {
            return PolicyFunctions.HuntHunterTooFar;
        }
        if (h.RouteSafePrey.Contains(prey.Source.ID))
        {
            return "";
        }
        if (h.RouteUnsafePrey.Contains(prey.Source.ID))
        {
            return PolicyFunctions.HuntHunterNoRoute;
        }
        if (h.RouteSkippedPrey.Contains(prey.Source.ID))
        {
            return PolicyFunctions.HuntHunterRouteSkipped;
        }
        return PolicyFunctions.HuntHunterRouteUnevaluated;
    }

    /// <summary>
    /// Decides whether the hunt row is offered: the first failing gate holds it. Gates in order: fogged, safe prey
    /// (a pest waives it), a running butcher bill and Cooking worker (a pest waives it), a colonist, then a
    /// colonist who can hunt it.
    /// </summary>
    public HuntVerdict Gate(HuntPrey prey)
    {
        HuntVerdict Hold(string reason, IReadOnlyList<string>? detail = null) =>
            new() { Hold = new HuntHold { ID = prey.Source.ID, Reason = reason, Detail = detail ?? [] } };
        if (prey.Fogged)
        {
            return Hold(PolicyFunctions.HuntHoldFogged);
        }
        if (!prey.IsPest())
        {
            if (prey.Mental || !prey.Source.Food)
            {
                return Hold(PolicyFunctions.HuntHoldNotSafePrey);
            }
            if (!ButcherReady(prey.Source.Resource))
            {
                return Hold(PolicyFunctions.HuntHoldNoButcher);
            }
        }
        if (Hunters.Count == 0)
        {
            return Hold(PolicyFunctions.HuntHoldNoColonist);
        }
        var detail = new List<string>();
        var reach = 0.0;
        var offered = false;
        foreach (var h in Hunters)
        {
            var reason = HunterReason(h, prey);
            if (reason.Length != 0)
            {
                detail.Add(h.ID + " " + reason);
                continue;
            }
            offered = true;
            if (h.Weapon is { } weapon && weapon.Hunts())
            {
                foreach (var v in weapon.Verbs)
                {
                    if (!v.Melee && v.AIWeapon)
                    {
                        reach = Math.Max(reach, v.Range);
                    }
                }
            }
        }
        if (!offered)
        {
            return Hold(PolicyFunctions.HuntHoldNoHunter, detail);
        }
        return new HuntVerdict { WeaponRange = reach };
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The damage worker class of incendiary shots.</summary>
    internal const string FlameDamageWorker = "Verse.DamageWorker_Flame";

    /// <summary>The squared distance within which a colonist hunts food prey (100 cells); a pest is hunted wherever it is on the map.</summary>
    internal const long HuntReachSquared = 10000;

    // Hunt hold reasons, stable words; no_hunter carries each colonist's own reason.
    public const string HuntHoldFogged = "fogged";
    public const string HuntHoldNotSafePrey = "not_safe_prey";
    public const string HuntHoldNoButcher = "no_butcher_bill";
    public const string HuntHoldNoColonist = "no_colonist";
    public const string HuntHoldNoHunter = "no_hunter";
    internal const string HuntHunterDowned = "downed";
    internal const string HuntHunterMental = "mental_state";
    internal const string HuntHunterInactive = "hunting_inactive";
    internal const string HuntHunterNoWeapon = "no_hunting_weapon";
    internal const string HuntHunterShield = "ranged_blocking_shield";
    internal const string HuntHunterTooFar = "too_far";
    internal const string HuntHunterNoRoute = "no_safe_route";

    /// <summary>Native's route budget ran out before this pair.</summary>
    internal const string HuntHunterRouteSkipped = "route_skipped";

    /// <summary>Native emitted no verdict for this pair.</summary>
    internal const string HuntHunterRouteUnevaluated = "route_unevaluated";
}
