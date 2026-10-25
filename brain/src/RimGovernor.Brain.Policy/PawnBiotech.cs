using RimGovernor.Contracts.Wire.Defs;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>The native Biotech block with catalog-derived gene effects and work ages.</summary>
public sealed record PawnBiotech
{
    public required O.PawnBiotech Observed { get; init; }
    public DevelopmentalStage DevelopmentalStage => Observed.DevelopmentalStage;
    public EquatableArray<O.PawnGene> Genes => Observed.Genes;
    public O.PawnMechanitor? Mechanitor => Observed.Mechanitor;
    public O.PawnMech? Mech => Observed.Mech;
    public O.PawnDeathrest? Deathrest => Observed.Deathrest;
    public GeneEffects Effects { get; init; } = new();
    public IReadOnlyDictionary<WorkType, int> WorkMinAges { get; init; } = new Dictionary<WorkType, int>();

    public bool IsChild() => DevelopmentalStage is DevelopmentalStage.Newborn or DevelopmentalStage.Baby or DevelopmentalStage.Child;
    public bool IsDeathresting() => Deathrest is { Deathresting: true };
}