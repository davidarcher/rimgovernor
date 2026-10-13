using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One pawn's Biotech facts as the row carries them. Every field is unknown when native did not read it; a
/// null inside a known fact is a known absence (a pawn that is no mechanitor, no mech, no deathrester). The
/// defs are the catalog's (bridge.BiotechCatalog).
/// </summary>
public sealed record PawnBiotech
{
    /// <summary>The LifeStageDef name.</summary>
    public Fact<string> LifeStage { get; init; }

    /// <summary>The DevelopmentalStage name (Newborn, Baby, Child, Adult, None).</summary>
    public Fact<string> DevelopmentalStage { get; init; }

    /// <summary>The learning need level (0-1); unknown for a pawn with no learning need.</summary>
    public Fact<double> Learning { get; init; }

    /// <summary>The LearningCategory name.</summary>
    public Fact<string> LearningCategory { get; init; }

    /// <summary>Every gene, endogene and xenogene.</summary>
    public Fact<IReadOnlyList<PawnGene>> Genes { get; init; }

    /// <summary>The XenotypeDef name, known "" for a custom or no xenotype.</summary>
    public Fact<string> Xenotype { get; init; }

    /// <summary>The xenotype label.</summary>
    public Fact<string> XenotypeName { get; init; }

    public Fact<bool> Hybrid { get; init; }

    /// <summary>The active genes' combined typed effects, resolved from the catalog; known whenever Genes is.</summary>
    public Fact<GeneEffects> Effects { get; init; }

    /// <summary>The race's minimum age in years per work type, resolved from the catalog; known only for a child.</summary>
    public Fact<IReadOnlyDictionary<WorkType, int>> WorkMinAges { get; init; }

    public Fact<PawnMechanitor?> Mechanitor { get; init; }

    public Fact<PawnMech?> Mech { get; init; }

    public Fact<PawnDeathrest?> Deathrest { get; init; }

    /// <summary>
    /// The ticks left on the pawn's XenogermReplicating hediff, known 0 with none. A pawn with regrow ticks
    /// left dies if extracted again.
    /// </summary>
    public Fact<int> XenogermRegrowTicksLeft { get; init; }

    /// <summary>The ticks left on the pawn's XenogerminationComa hediff, known 0 with none.</summary>
    public Fact<int> XenogermComaTicksLeft { get; init; }

    /// <summary>A pawn held by a gene extractor.</summary>
    public Fact<bool> InExtractor { get; init; }

    /// <summary>
    /// The game's Building_GeneExtractor.CanAcceptPawn verdict (true when any owned extractor of the map
    /// accepts). Unknown with no extractor, off the map or when the read failed.
    /// </summary>
    public Fact<bool> Extractable { get; init; }

    /// <summary>The first refusal text, known "" when the game gave none.</summary>
    public Fact<string> ExtractableReason { get; init; }

    /// <summary>Reports whether the developmental stage is known and is a pre-adult one (Newborn, Baby, Child).</summary>
    public (bool Child, bool Known) IsChild()
    {
        if (!DevelopmentalStage.TryGetValue(out var stage))
        {
            return (false, false);
        }
        return stage is "Newborn" or "Baby" or "Child" ? (true, true) : (false, true);
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

    public Fact<bool> Xenogene { get; init; }

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
}

/// <summary>
/// A controllable mech's overseer (known "" when it has none), work mode (MechWorkModeDef name) and control
/// group index. Energy is the Need_MechEnergy level (0-1); RechargeBelow and RechargeAbove are the control
/// group's own recharge band (mechRechargeThresholds, 0-1).
/// </summary>
public sealed record PawnMech
{
    public string Overseer { get; init; } = "";

    public Fact<string> WorkMode { get; init; }

    public Fact<int> ControlGroup { get; init; }

    public Fact<double> Energy { get; init; }

    public Fact<double> RechargeBelow { get; init; }

    public Fact<double> RechargeAbove { get; init; }
}

/// <summary>A deathrester's state.</summary>
public sealed record PawnDeathrest
{
    public Fact<bool> Deathresting { get; init; }

    public Fact<double> Level { get; init; }

    public Fact<int> LastDeathrestTick { get; init; }

    public Fact<double> DeathrestPercent { get; init; }

    public Fact<int> Capacity { get; init; }

    public Fact<int> BoundBuildings { get; init; }

    public Fact<bool> AutoWake { get; init; }
}
