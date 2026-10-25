using RimGovernor.Brain.GameFacts.Defs;

namespace RimGovernor.Brain.Policy;

/// <summary>The native Biotech block with catalog-derived gene effects and work ages.</summary>
public sealed record PawnBiotech
{
    public required RimGovernor.Brain.GameFacts.Observations.PawnBiotech Observed { get; init; }
    public DevelopmentalStage DevelopmentalStage => Observed.DevelopmentalStage;
    public EquatableArray<RimGovernor.Brain.GameFacts.Observations.PawnGene> Genes => Observed.Genes;
    public RimGovernor.Brain.GameFacts.Observations.PawnMechanitor? Mechanitor => Observed.Mechanitor;
    public RimGovernor.Brain.GameFacts.Observations.PawnMech? Mech => Observed.Mech;
    public RimGovernor.Brain.GameFacts.Observations.PawnDeathrest? Deathrest => Observed.Deathrest;
    public GeneEffects Effects { get; init; } = new();
    public IReadOnlyDictionary<WorkType, int> WorkMinAges { get; init; } = new Dictionary<WorkType, int>();

    public bool IsChild() => DevelopmentalStage is DevelopmentalStage.Newborn or DevelopmentalStage.Baby or DevelopmentalStage.Child;
    public bool IsDeathresting() => Deathrest is { Deathresting: true };
}