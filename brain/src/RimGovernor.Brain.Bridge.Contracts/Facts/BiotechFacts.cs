using RimGovernor.Protocol.Observations;
using PolicyPawnBiotech = RimGovernor.Brain.Policy.PawnBiotech;
using PolicyPawnDeathrest = RimGovernor.Brain.Policy.PawnDeathrest;
using PolicyPawnGene = RimGovernor.Brain.Policy.PawnGene;
using PolicyPawnMech = RimGovernor.Brain.Policy.PawnMech;
using PolicyPawnMechanitor = RimGovernor.Brain.Policy.PawnMechanitor;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go biotech.go: the lift of a pawn's Biotech block. The block validator (validatePawnBiotech) belongs to the pawn
// row decoder and is not ported. The gene effects and work minimum ages the lift leaves unknown are resolved
// against the catalog by the caller.
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>PawnBiotech</c>: lifts a pawn row's Biotech block; unknown when the row carries none (no Biotech).
    /// A field a read issue names stays unknown.
    /// </summary>
    public static Fact<PolicyPawnBiotech> PawnBiotech(RimGovernor.Protocol.Observations.PawnBiotech? biotech)
    {
        if (biotech is null)
        {
            return Unknown<PolicyPawnBiotech>();
        }
        var failed = FailedFields(biotech.Issues);
        var result = new PolicyPawnBiotech
        {
            LifeStage = OptionalFact(biotech.HasLifeStage, biotech.LifeStage),
            DevelopmentalStage = OptionalFact(biotech.HasDevelopmentalStage, biotech.DevelopmentalStage),
            Learning = OptionalFact(biotech.HasLearning, biotech.Learning),
            LearningCategory = OptionalFact(biotech.HasLearningCategory, biotech.LearningCategory),
        };
        if (!failed.Contains("genes") && biotech.HasXenotypeName)
        {
            // Genes exist only on a pawn with a gene tracker; the xenotype name is read with them.
            EquatableArray<PolicyPawnGene> genes =
            [
                .. biotech.Genes.Select(gene => new PolicyPawnGene { Name = gene.DefName, Xenogene = Known(gene.Xenogene), Active = Known(gene.Active) }),
            ];
            result = result with
            {
                Genes = Known(genes),
                Xenotype = Known(biotech.Xenotype),
                XenotypeName = Known(biotech.XenotypeName),
                Hybrid = OptionalFact(biotech.HasHybrid, biotech.Hybrid),
            };
        }
        if (!failed.Contains("mechanitor"))
        {
            PolicyPawnMechanitor? mechanitor = biotech.Mechanitor is { } v
                ? new PolicyPawnMechanitor
                {
                    UsedBandwidth = OptionalInt(v.HasUsedBandwidth, v.UsedBandwidth),
                    TotalBandwidth = OptionalInt(v.HasTotalBandwidth, v.TotalBandwidth),
                    GestationBandwidth = OptionalInt(v.HasUsedBandwidthFromGestation, v.UsedBandwidthFromGestation),
                    ControlGroups = OptionalInt(v.HasControlGroups, v.ControlGroups),
                    ControlledMechs = [.. v.ControlledMechs.Select(mech => mech.Id)],
                }
                : null;
            result = result with { Mechanitor = Known(mechanitor) };
        }
        if (!failed.Contains("mech"))
        {
            PolicyPawnMech? mech = null;
            if (biotech.Mech is { } v)
            {
                mech = new PolicyPawnMech
                {
                    Overseer = v.Overseer?.Id ?? "",
                    WorkMode = OptionalFact(v.HasWorkMode, v.WorkMode),
                    ControlGroup = OptionalInt(v.HasControlGroup, v.ControlGroup),
                    Energy = OptionalFact(v.HasEnergy, v.Energy),
                };
                if (!failed.Contains("mech_thresholds"))
                {
                    mech = mech with
                    {
                        RechargeBelow = OptionalFact(v.HasRechargeBelow, v.RechargeBelow),
                        RechargeAbove = OptionalFact(v.HasRechargeAbove, v.RechargeAbove),
                    };
                }
            }
            result = result with { Mech = Known(mech) };
        }
        if (!failed.Contains("deathrest"))
        {
            PolicyPawnDeathrest? deathrest = biotech.Deathrest is { } v
                ? new PolicyPawnDeathrest
                {
                    Deathresting = OptionalFact(v.HasDeathresting, v.Deathresting),
                    Level = OptionalFact(v.HasLevel, v.Level),
                    LastDeathrestTick = OptionalInt(v.HasLastDeathrestTick, v.LastDeathrestTick),
                    DeathrestPercent = OptionalFact(v.HasDeathrestPercent, v.DeathrestPercent),
                    Capacity = OptionalInt(v.HasCapacity, v.Capacity),
                    BoundBuildings = OptionalInt(v.HasBoundBuildings, v.BoundBuildings),
                    AutoWake = OptionalFact(v.HasAutoWake, v.AutoWake),
                }
                : null;
            result = result with { Deathrest = Known(deathrest) };
        }
        if (!failed.Contains("gene_lifecycle"))
        {
            result = result with
            {
                XenogermRegrowTicksLeft = OptionalInt(biotech.HasXenogermRegrowTicksLeft, biotech.XenogermRegrowTicksLeft),
                XenogermComaTicksLeft = OptionalInt(biotech.HasXenogermComaTicksLeft, biotech.XenogermComaTicksLeft),
                InExtractor = OptionalFact(biotech.HasInExtractor, biotech.InExtractor),
            };
        }
        if (!failed.Contains("extractable") && biotech.HasExtractable)
        {
            result = result with { Extractable = Known(biotech.Extractable), ExtractableReason = Known(biotech.ExtractableReason) };
        }
        return Known(result);
    }
}
