namespace RimGovernor.Brain.Policy;

// Go: burn.go
// Burning the incinerator: the room is meant to be full and burned whole, so MaintainIncineration lights it
// once per batch, never per item. PlanBurn decides one burn from the census; the planner turns a ready order
// into equip (when the burner holds no molotov), draft and ignite with the existing action kinds, the combat
// planner's undraft releasing the burner once the plan settles.

/// <summary>Why a burn is or is not ordered.</summary>
public enum BurnVerdict
{
    [WireName("ready")]
    Ready,

    [WireName("below_batch")]
    BelowBatch,

    [WireName("fire_active")]
    FireActive,

    [WireName("room_occupied")]
    Occupied,

    [WireName("no_burner")]
    NoBurner,

    [WireName("no_firefighting_standby")]
    NoStandby,

    [WireName("no_molotov")]
    NoMolotov,
}

/// <summary>One colonist as a burner and as a firefighter.</summary>
public sealed record BurnCandidate
{
    public required EquipCandidatePawn Arm { get; init; }

    public required FireSafetyPawnFacts Fire { get; init; }

    /// <summary>A molotov in the primary slot already.</summary>
    public bool Holds { get; init; }
}

/// <summary>The incinerator's census. <see cref="Fire"/> is true while any fire is burning or the fire census is unread.</summary>
public sealed record BurnRequest
{
    public Rectangle Interior { get; init; }

    public int Stored { get; init; }

    public bool Fire { get; init; }

    public EquatableArray<BurnCandidate> Pawns { get; init; }

    /// <summary>The loose molotovs on the map.</summary>
    public EquatableArray<EquipCandidateWeapon> Molotovs { get; init; }

    internal bool Inside(Cell c)
    {
        var i = Interior;
        return c.X >= i.X && c.X < i.X + i.Width && c.Z >= i.Z && c.Z < i.Z + i.Height;
    }

    /// <summary>The lowest-ID loose molotov the burner may carry.</summary>
    internal EquipCandidateWeapon? MolotovFor(Domain.PawnId burner)
    {
        EquipCandidateWeapon? best = null;
        foreach (var w in Molotovs.Items)
        {
            if (w.Definition != PolicyFunctions.MolotovDef || (w.Biocoded && w.BiocodedTo != burner))
            {
                continue;
            }

            if (best is null || string.CompareOrdinal(w.Thing, best.Thing) < 0)
            {
                best = w;
            }
        }

        return best;
    }
}

/// <summary>
/// A ready burn: the burner, the loose molotov it picks up (null when it holds one) and the cell it throws
/// at, the room's centre.
/// </summary>
public sealed record BurnOrder
{
    public Domain.PawnId Burner { get; init; }

    public EquipCandidateWeapon? Molotov { get; init; }

    public Cell Target { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>The burner's weapon, stocked loose by the armory.</summary>
    public const string MolotovDef = "Weapon_GrenadeMolotov";

    /// <summary>The filth a burned room leaves behind.</summary>
    public const string AshDef = "Filth_Ash";

    /// <summary>The batch: interior cells holding things (two thirds of the 3x3 room) before the room is lit.</summary>
    public const int BurnStoredCells = 6;

    /// <summary>
    /// Orders one burn when the batch is met, nothing burns and no colonist stands in the room. The burner is
    /// the lowest-ID colonist able to fire a molotov (a holder first), and only while another colonist stands
    /// by to fight fire. Native does not re-check occupancy; this census (colonists) is the only guard.
    /// </summary>
    public static (BurnOrder Order, BurnVerdict Verdict) PlanBurn(BurnRequest r)
    {
        if (r.Stored < BurnStoredCells)
        {
            return (new BurnOrder(), BurnVerdict.BelowBatch);
        }

        if (r.Fire)
        {
            return (new BurnOrder(), BurnVerdict.FireActive);
        }

        foreach (var p in r.Pawns.Items)
        {
            if (r.Inside(p.Arm.Position))
            {
                return (new BurnOrder(), BurnVerdict.Occupied);
            }
        }

        var pawns = r.Pawns.Items.OrderByDescending(p => p.Holds).ThenBy(p => p.Arm.Pawn.Value, StringComparer.Ordinal).ToList();
        var verdict = BurnVerdict.NoBurner;
        foreach (var burner in pawns)
        {
            if (!CanBurn(burner.Arm))
            {
                continue;
            }

            if (!Standby(pawns, burner.Arm.Pawn))
            {
                verdict = BurnVerdict.NoStandby;
                continue;
            }

            var order = new BurnOrder
            {
                Burner = burner.Arm.Pawn,
                Target = new Cell(r.Interior.X + (r.Interior.Width / 2), r.Interior.Z + (r.Interior.Height / 2)),
            };
            if (burner.Holds)
            {
                return (order, BurnVerdict.Ready);
            }

            var molotov = r.MolotovFor(burner.Arm.Pawn);
            if (molotov is null)
            {
                return (new BurnOrder(), BurnVerdict.NoMolotov);
            }

            return (order with { Molotov = molotov }, BurnVerdict.Ready);
        }

        return (new BurnOrder(), verdict);
    }

    /// <summary>A colonist standing, free, able to fire and allowed arms.</summary>
    private static bool CanBurn(EquipCandidatePawn p)
    {
        var dk = p.Dead.TryGetValue(out var dead);
        var wk = p.Downed.TryGetValue(out var downed);
        var tk = p.Drafted.TryGetValue(out var drafted);
        var mk = p.MentalState.TryGetValue(out var mental);
        var vk = p.IncapableOfViolence.TryGetValue(out var incapable);
        return dk && wk && tk && mk && vk && !dead && !downed && !drafted && !mental && !incapable && !p.ShootingDisabled && p.NoArms.Length == 0;
    }

    /// <summary>A colonist other than the burner who would fight a fire.</summary>
    private static bool Standby(IEnumerable<BurnCandidate> pawns, Domain.PawnId burner) =>
        pawns.Any(p => p.Arm.Pawn != burner && FireSafetyEligible(p.Fire));

    /// <summary>The ash lying in the interior, by ID: the cleanup a burn leaves.</summary>
    public static List<UpkeepFilth> IncineratorAsh(IEnumerable<UpkeepFilth> filth, Rectangle interior)
    {
        var room = new BurnRequest { Interior = interior };
        return [.. filth.Where(f => f.Definition == AshDef && room.Inside(f.Cell)).OrderBy(f => f.Id, StringComparer.Ordinal)];
    }
}
