using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire.Defs;
using O = RimGovernor.Contracts.Wire.Observations;
using Wire = RimGovernor.Contracts.Wire.Defs;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One pawn's Biotech facts as the row carries them. The row is read whole; a null inside a known fact is a known
/// absence (a pawn that is no mechanitor, no mech, no deathrester). Effects and WorkMinAges are resolved from the
/// catalog (bridge.BiotechCatalog) by the caller.
/// </summary>
public sealed record PawnBiotech
{
    /// <summary>The game's DevelopmentalStage (Newborn, Baby, Child, Adult, None).</summary>
    public required DevelopmentalStage DevelopmentalStage { get; init; }

    /// <summary>Every gene, endogene and xenogene; empty for a pawn without a gene tracker.</summary>
    public required EquatableArray<PawnGene> Genes { get; init; }

    /// <summary>The active genes' combined typed effects, resolved from the catalog by the work read.</summary>
    public Fact<GeneEffects> Effects { get; init; }

    /// <summary>The race's minimum age in years per work type, resolved from the catalog; known only for a child.</summary>
    public Fact<IReadOnlyDictionary<WorkType, int>> WorkMinAges { get; init; }

    /// <summary>The pawn's mechanitor tracker; null for a pawn that is no mechanitor.</summary>
    public required PawnMechanitor? Mechanitor { get; init; }

    /// <summary>The mech the player can ever control; null for a pawn that is no such mech.</summary>
    public required PawnMech? Mech { get; init; }

    /// <summary>The deathrest gene's state; null for a pawn without one.</summary>
    public required PawnDeathrest? Deathrest { get; init; }

    /// <summary>The pawn row's Biotech block as the policy reads it.</summary>
    public static PawnBiotech Of(O.PawnBiotech b) => new()
    {
        DevelopmentalStage = b.DevelopmentalStage,
        Genes = EquatableArray.From(b.Genes.Select(g => new PawnGene { Name = g.DefName, Active = g.Active })),
        Mechanitor = b.Mechanitor is { } m ? PawnMechanitor.Of(m) : null,
        Mech = b.Mech is { } mech ? PawnMech.Of(mech) : null,
        Deathrest = b.Deathrest is { } d ? new PawnDeathrest { Deathresting = d.Deathresting, Capacity = d.Capacity } : null,
    };

    /// <summary>Whether the developmental stage is a pre-adult one (Newborn, Baby, Child).</summary>
    public bool IsChild() => DevelopmentalStage is Wire.DevelopmentalStage.Newborn or Wire.DevelopmentalStage.Baby or Wire.DevelopmentalStage.Child;

    /// <summary>Whether the pawn is deathresting: a pawn in deathrest takes neither work nor fights.</summary>
    public bool IsDeathresting() => Deathrest is { Deathresting: true };
}

/// <summary>One gene of a pawn; Active is false while another gene overrides it.</summary>
public sealed record PawnGene
{
    public required string Name { get; init; }

    public required bool Active { get; init; }
}

/// <summary>A mechanitor's bandwidth and control groups.</summary>
public sealed record PawnMechanitor
{
    public required int UsedBandwidth { get; init; }

    public required int TotalBandwidth { get; init; }

    public required int GestationBandwidth { get; init; }

    public required int ControlGroups { get; init; }

    /// <summary>The mechs under the pawn's control.</summary>
    public required IReadOnlyList<string> ControlledMechs { get; init; }

    internal static PawnMechanitor Of(O.PawnMechanitor m) => new()
    {
        UsedBandwidth = m.UsedBandwidth,
        TotalBandwidth = m.TotalBandwidth,
        GestationBandwidth = m.UsedBandwidthFromGestation,
        ControlGroups = m.ControlGroups,
        ControlledMechs = [.. m.ControlledMechs.Select(r => r.Id ?? "")],
    };
}

/// <summary>
/// A controllable mech: its overseer ("" when it has none), work mode (a MechWorkModeDef name; null without one),
/// energy and control group (null outside one).
/// </summary>
public sealed record PawnMech
{
    public required string Overseer { get; init; }

    public required string? WorkMode { get; init; }

    public required MechEnergy Energy { get; init; }

    public required PawnMechGroup? Group { get; init; }

    internal static PawnMech Of(O.PawnMech m) => new()
    {
        Overseer = m.Overseer?.Id ?? "",
        WorkMode = m.WorkMode,
        Energy = m.Energy is O.PawnMechEnergy.Level level ? new MechEnergy.Level(level.Fraction) : new MechEnergy.NoEnergyNeed(),
        Group = m.Group is { } g ? new PawnMechGroup(g.Index, g.RechargeBelow, g.RechargeAbove) : null,
    };
}

/// <summary>A mech's energy: the Need_MechEnergy level (0-1), or none for a mech with no energy need.</summary>
public abstract record MechEnergy
{
    private MechEnergy()
    {
    }

    public sealed record Level(double Fraction) : MechEnergy;

    public sealed record NoEnergyNeed : MechEnergy;
}

/// <summary>A mech's control group: its index and the group's own recharge band (mechRechargeThresholds, 0-1).</summary>
public sealed record PawnMechGroup(int Index, double RechargeBelow, double RechargeAbove);

/// <summary>A deathrester's state: whether it deathrests now and its deathrest capacity.</summary>
public sealed record PawnDeathrest
{
    public required bool Deathresting { get; init; }

    public required int Capacity { get; init; }
}
