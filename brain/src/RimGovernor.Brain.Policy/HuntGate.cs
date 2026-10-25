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
    public HuntProjectile Projectile { get; init; }
    public string DamageDef { get; init; } = "";
    public string DamageWorker { get; init; } = "";
}

/// <summary>A hunter's primary weapon, raw; a hunter without one is unarmed.</summary>
public sealed record HuntWeapon
{
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
            if (v.Projectile != HuntProjectile.Bullet || v.ExplosionRadius != 0 || v.DamageDef.Length == 0 || v.DamageWorker == HuntGateRules.FlameDamageWorker)
            {
                return false;
            }
        }
        return n > 0;
    }
}

/// <summary>
/// One free colonist's raw hunting facts. HuntingActive is the game's WorkIsActive; RangedBlockingShield mirrors
/// the game's own shield check. The Route*Prey sets carry native's per-row route verdict.
/// </summary>
public sealed record HuntHunter
{
    public required RimGovernor.Brain.GameFacts.Observations.HunterFacts Observed { get; init; }
    public string ID => Observed.Pawn.Value;
    public Cell Cell => Observed.Position;
    public bool Downed => Observed.Downed;
    public bool MentalState => Observed.InMentalState;
    public bool HuntingActive => Observed.HuntingActive;
    public HuntWeapon? Weapon { get; init; }
    public bool RangedBlockingShield => Observed.RangedBlockingShield;
}

/// <summary>A hunt row policy offers no hunt for: the first gate it fails, and for no_hunter each colonist's reason ("&lt;id&gt; &lt;reason&gt;").</summary>
public sealed record HuntHold
{
    public string ID { get; init; } = "";
    public string Reason { get; init; } = "";
    public IReadOnlyList<string> Detail { get; init; } = [];

    /// <summary>The held prey, set by the census decoder, so the plan can price the prerequisite its hold waits on (HuntPrerequisiteCandidates).</summary>
    public required AcquisitionSource Source { get; init; }
}

/// <summary>A hunt row with the raw prey flags that are not part of the source: fogged and in a mental state.</summary>
public sealed record HuntPrey
{
    public required AcquisitionSource Source { get; init; }
    public bool Fogged => Source.Observed.Kind is RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Hunt { Fogged: true };
    public bool Mental => Source.Observed.Kind is RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Hunt { InMentalState: true };

    /// <summary>The pest rule: a pest race that is not in a mental state.</summary>
    internal bool IsPest() => Source.Pest && !Mental;
}

/// <summary>The gate's answer for one hunt row. A null Hold offers the row, with WeaponRange the longest reach among the colonists who can hunt it with a ranged weapon.</summary>
public sealed record HuntVerdict
{
    public HuntHold? Hold { get; init; }
    public double WeaponRange { get; init; }
}

/// <summary>Native's raw hunt facts: every free colonist.</summary>
public sealed record HuntCensus
{
    public IReadOnlyList<HuntHunter> Hunters { get; init; } = [];

    /// <summary>Why a colonist cannot hunt the prey, empty when they can; order is the gate's: downed, mental state, Hunting off, weapon (a shield blocks a ranged one), reach.</summary>
    private static string HunterReason(HuntHunter h, HuntPrey prey)
    {
        var meleeable = prey.Source.MeleeOnly;
        var shooter = h.Weapon is not null && h.Weapon.Hunts();
        var armed = (shooter && !h.RangedBlockingShield) || (meleeable && (h.Weapon is null || h.Weapon.Melee));
        long dx = h.Cell.X - prey.Source.Cell.X;
        long dz = h.Cell.Z - prey.Source.Cell.Z;
        if (h.Downed)
        {
            return HuntGateRules.HuntHunterDowned;
        }
        if (h.MentalState)
        {
            return HuntGateRules.HuntHunterMental;
        }
        if (!h.HuntingActive)
        {
            return HuntGateRules.HuntHunterInactive;
        }
        if (!armed && shooter)
        {
            return HuntGateRules.HuntHunterShield;
        }
        if (!armed)
        {
            return HuntGateRules.HuntHunterNoWeapon;
        }
        if (!prey.IsPest() && dx * dx + dz * dz > HuntGateRules.HuntReachSquared)
        {
            return HuntGateRules.HuntHunterTooFar;
        }
        return "";
    }

    /// <summary>
    /// Decides whether the hunt row is offered: the first failing gate holds it. Gates in order: fogged, safe prey
    /// (a pest waives it), a colonist, then a
    /// colonist who can hunt it.
    /// </summary>
    public HuntVerdict Gate(HuntPrey prey)
    {
        HuntVerdict Hold(string reason, IReadOnlyList<string>? detail = null) =>
            new() { Hold = new HuntHold { Source = prey.Source, ID = prey.Source.Id, Reason = reason, Detail = detail ?? [] } };
        if (prey.Fogged)
        {
            return Hold(HuntGateRules.HuntHoldFogged);
        }
        if (!prey.IsPest())
        {
            if (prey.Mental || !prey.Source.Food)
            {
                return Hold(HuntGateRules.HuntHoldNotSafePrey);
            }
        }
        if (Hunters.Count == 0)
        {
            return Hold(HuntGateRules.HuntHoldNoColonist);
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
            return Hold(HuntGateRules.HuntHoldNoHunter, detail);
        }
        return new HuntVerdict { WeaponRange = reach };
    }
}

public static class HuntGateRules
{
    /// <summary>The damage worker class of incendiary shots.</summary>
    internal const string FlameDamageWorker = "Verse.DamageWorker_Flame";

    /// <summary>The squared distance within which a colonist hunts food prey (100 cells); a pest is hunted wherever it is on the map.</summary>
    internal const long HuntReachSquared = 10000;

    // Hunt hold reasons, stable words; no_hunter carries each colonist's own reason.
    public const string HuntHoldFogged = "fogged";
    public const string HuntHoldNotSafePrey = "not_safe_prey";
    public const string HuntHoldNoColonist = "no_colonist";
    public const string HuntHoldNoHunter = "no_hunter";
    internal const string HuntHunterDowned = "downed";
    internal const string HuntHunterMental = "mental_state";
    internal const string HuntHunterInactive = "hunting_inactive";
    internal const string HuntHunterNoWeapon = "no_hunting_weapon";
    internal const string HuntHunterShield = "ranged_blocking_shield";
    internal const string HuntHunterTooFar = "too_far";
}
