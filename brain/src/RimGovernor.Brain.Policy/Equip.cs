namespace RimGovernor.Brain.Policy;

/// <summary>
/// Describes an observed colonist and optional combat facts. Selection is a proposal; native admission
/// rechecks the exact pair. (Go <c>EquipCandidatePawn</c>.)
/// </summary>
public sealed record EquipCandidatePawn
{
    public Domain.PawnId Pawn { get; init; }

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Downed { get; init; }

    public Fact<bool> Drafted { get; init; }

    public Fact<bool> MentalState { get; init; }

    public Fact<bool> IncapableOfViolence { get; init; }

    public Fact<bool> Armed { get; init; }

    public Cell Position { get; init; }

    public PawnProfile Profile { get; init; } = new();

    public bool ShootingDisabled { get; init; }

    public WeaponRole Role { get; init; }

    /// <summary>Must be explicitly known before assigning area-fire weapons.</summary>
    public bool LoneFighter { get; init; }

    public Fact<double> RaidArmor { get; init; }

    /// <summary>The equipped primary: the bot owns every equipment decision.</summary>
    public EquipCandidateWeapon? Current { get; init; }

    /// <summary>
    /// The plain-English reason the pawn must not hold a weapon yet, empty when nothing holds it back (a
    /// creepjoiner whose downside has not shown). Every weapon decision leaves such a pawn unarmed.
    /// </summary>
    public string NoArms { get; init; } = "";
}

/// <summary>Describes one already-observed loose weapon. (Go <c>EquipCandidateWeapon</c>.)</summary>
public sealed record EquipCandidateWeapon
{
    public string Thing { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Cell { get; init; }

    public WeaponClass Class { get; init; }

    public Domain.PawnId BiocodedTo { get; init; }

    public bool Biocoded { get; init; }

    /// <summary>The weapon def's rows.</summary>
    public WeaponDef Facts { get; init; } = new();
}

/// <summary>
/// Ranks a loose equippable by what it is for. The native "weapons" census is ThingDef.IsWeapon, which includes
/// anything a pawn can swing (a wood log, a beer). The class is the definition's observed flags, never its name.
/// </summary>
public enum WeaponClass
{
    /// <summary>Equippable but not a weapon by trade (WoodLog, Beer).</summary>
    Makeshift,

    /// <summary>IsMeleeWeapon and in the Weapons category.</summary>
    Melee,

    /// <summary>IsRangedWeapon; what hunting needs.</summary>
    Ranged,
}

public static partial class PolicyFunctions
{
    /// <summary>Go <c>ClassifyWeapon</c>: maps the observed definition flags onto a class.</summary>
    public static WeaponClass ClassifyWeapon(bool byTrade, bool ranged, bool melee)
    {
        if (ranged)
        {
            return WeaponClass.Ranged;
        }
        if (melee && byTrade)
        {
            return WeaponClass.Melee;
        }
        return WeaponClass.Makeshift;
    }

    /// <summary>
    /// Go <c>SelectEquip</c>: returns the first pair from the colony-wide assignment, or null when none. Call
    /// <c>AssignEquip</c> to dispatch the whole wave. Admission remains native.
    /// </summary>
    public static (Domain.PawnId Pawn, EquipCandidateWeapon Weapon)? SelectEquip(
        IReadOnlyList<EquipCandidatePawn> pawns,
        IReadOnlyList<EquipCandidateWeapon> weapons
    )
    {
        var pairs = AssignEquip(pawns, weapons);
        if (pairs.Count == 0)
        {
            return null;
        }
        return ((Domain.PawnId)pairs[0].Pawn, pairs[0].Weapon);
    }

    internal static long DistanceSquared(Cell a, Cell b)
    {
        long dx = a.X - b.X;
        long dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz);
    }
}
