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
    public Fact<DevelopmentalStage> DevelopmentalStage { get; init; }

    /// <summary>Every gene, endogene and xenogene; empty for a pawn without a gene tracker.</summary>
    public Fact<EquatableArray<PawnGene>> Genes { get; init; }

    /// <summary>The active genes' combined typed effects, resolved from the catalog; known whenever Genes is.</summary>
    public Fact<GeneEffects> Effects { get; init; }

    /// <summary>The race's minimum age in years per work type, resolved from the catalog; known only for a child.</summary>
    public Fact<IReadOnlyDictionary<WorkType, int>> WorkMinAges { get; init; }

    public Fact<PawnMechanitor?> Mechanitor { get; init; }

    public Fact<PawnMech?> Mech { get; init; }

    public Fact<PawnDeathrest?> Deathrest { get; init; }

    /// <summary>The pawn row's Biotech block as the policy reads it.</summary>
    public static PawnBiotech Of(O.PawnBiotech b) => new()
    {
        DevelopmentalStage = Known(b.DevelopmentalStage),
        Genes = Known(EquatableArray.From(b.Genes.Select(g => new PawnGene { Name = g.DefName, Active = Known(g.Active) }))),
        Mechanitor = Known(b.Mechanitor is { } m ? PawnMechanitor.Of(m) : null),
        Mech = Known(b.Mech is { } mech ? PawnMech.Of(mech) : null),
        Deathrest = Known(b.Deathrest is { } d ? new PawnDeathrest { Deathresting = Known(d.Deathresting), Capacity = Known(d.Capacity) } : null),
    };

    /// <summary>Reports whether the developmental stage is known and is a pre-adult one (Newborn, Baby, Child).</summary>
    public (bool Child, bool Known) IsChild()
    {
        if (!DevelopmentalStage.TryGetValue(out var stage))
        {
            return (false, false);
        }
        return stage is Wire.DevelopmentalStage.Newborn or Wire.DevelopmentalStage.Baby or Wire.DevelopmentalStage.Child ? (true, true) : (false, true);
    }

    /// <summary>
    /// Reports whether the pawn is known to be deathresting: a pawn in deathrest takes neither work nor
    /// fights. An unknown deathrest read counts as not resting.
    /// </summary>
    public bool IsDeathresting() =>
        Deathrest.TryGetValue(out var d) && d is not null && d.Deathresting.TryGetValue(out var resting) && resting;
}

/// <summary>One gene of a pawn; Active is false while another gene overrides it.</summary>
public sealed record PawnGene
{
    public string Name { get; init; } = "";

    public Fact<bool> Active { get; init; }
}

/// <summary>A mechanitor's bandwidth and control groups.</summary>
public sealed record PawnMechanitor
{
    public Fact<int> UsedBandwidth { get; init; }

    public Fact<int> TotalBandwidth { get; init; }

    public Fact<int> GestationBandwidth { get; init; }

    public Fact<int> ControlGroups { get; init; }

    /// <summary>The mechs under the pawn's control.</summary>
    public IReadOnlyList<string> ControlledMechs { get; init; } = [];

    internal static PawnMechanitor Of(O.PawnMechanitor m) => new()
    {
        UsedBandwidth = Known(m.UsedBandwidth),
        TotalBandwidth = Known(m.TotalBandwidth),
        GestationBandwidth = Known(m.UsedBandwidthFromGestation),
        ControlGroups = Known(m.ControlGroups),
        ControlledMechs = [.. m.ControlledMechs.Select(r => r.Id ?? "")],
    };
}

/// <summary>
/// A controllable mech's overseer (known "" when it has none), work mode (MechWorkModeDef name) and control
/// group index. Energy is the Need_MechEnergy level (0-1); RechargeBelow and RechargeAbove are the control
/// group's own recharge band (mechRechargeThresholds, 0-1). A mech with no work mode, energy need or control group
/// leaves that fact unknown.
/// </summary>
public sealed record PawnMech
{
    public string Overseer { get; init; } = "";

    public Fact<string> WorkMode { get; init; }

    public Fact<int> ControlGroup { get; init; }

    public Fact<double> Energy { get; init; }

    public Fact<double> RechargeBelow { get; init; }

    public Fact<double> RechargeAbove { get; init; }

    internal static PawnMech Of(O.PawnMech m) => new()
    {
        Overseer = m.Overseer?.Id ?? "",
        WorkMode = m.WorkMode is { } mode ? Known(mode) : default,
        Energy = m.Energy is { } energy ? Known(energy) : default,
        ControlGroup = m.Group is { } g ? Known(g.Index) : default,
        RechargeBelow = m.Group is { } below ? Known(below.RechargeBelow) : default,
        RechargeAbove = m.Group is { } above ? Known(above.RechargeAbove) : default,
    };
}

/// <summary>A deathrester's state: whether it deathrests now and its deathrest capacity.</summary>
public sealed record PawnDeathrest
{
    public Fact<bool> Deathresting { get; init; }

    public Fact<int> Capacity { get; init; }
}
