using RimGovernor.Contracts.Wire.Observations;
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
    public static Fact<PolicyPawnBiotech> PawnBiotech(RimGovernor.Contracts.Wire.Observations.PawnBiotech? biotech)
    {
        if (biotech is null)
        {
            return Unknown<PolicyPawnBiotech>();
        }
        var failed = FailedFields(biotech.Issues);
        var result = new PolicyPawnBiotech
        {
            LifeStage = OptionalFact(biotech.LifeStage is not null, (biotech.LifeStage ?? "")),
            DevelopmentalStage = OptionalFact(biotech.DevelopmentalStage is not null, (biotech.DevelopmentalStage ?? "")),
            Learning = OptionalFact(biotech.Learning is not null, biotech.Learning.GetValueOrDefault()),
            LearningCategory = OptionalFact(biotech.LearningCategory is not null, (biotech.LearningCategory ?? "")),
        };
        if (!failed.Contains("genes") && biotech.XenotypeName is not null)
        {
            // Genes exist only on a pawn with a gene tracker; the xenotype name is read with them.
            EquatableArray<PolicyPawnGene> genes =
            [
                .. biotech.Genes.Select(gene => new PolicyPawnGene { Name = gene.DefName ?? "", Xenogene = Known(gene.Xenogene.GetValueOrDefault()), Active = Known(gene.Active.GetValueOrDefault()) }),
            ];
            result = result with
            {
                Genes = Known(genes),
                Xenotype = Known((biotech.Xenotype ?? "")),
                XenotypeName = Known((biotech.XenotypeName ?? "")),
                Hybrid = OptionalFact(biotech.Hybrid is not null, biotech.Hybrid.GetValueOrDefault()),
            };
        }
        if (!failed.Contains("mechanitor"))
        {
            PolicyPawnMechanitor? mechanitor = biotech.Mechanitor is { } v
                ? new PolicyPawnMechanitor
                {
                    UsedBandwidth = OptionalInt(v.UsedBandwidth is not null, v.UsedBandwidth.GetValueOrDefault()),
                    TotalBandwidth = OptionalInt(v.TotalBandwidth is not null, v.TotalBandwidth.GetValueOrDefault()),
                    GestationBandwidth = OptionalInt(v.UsedBandwidthFromGestation is not null, v.UsedBandwidthFromGestation.GetValueOrDefault()),
                    ControlGroups = OptionalInt(v.ControlGroups is not null, v.ControlGroups.GetValueOrDefault()),
                    ControlledMechs = [.. v.ControlledMechs.Select(mech => (mech.Id ?? ""))],
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
                    WorkMode = OptionalFact(v.WorkMode is not null, (v.WorkMode ?? "")),
                    ControlGroup = OptionalInt(v.ControlGroup is not null, v.ControlGroup.GetValueOrDefault()),
                    Energy = OptionalFact(v.Energy is not null, v.Energy.GetValueOrDefault()),
                };
                if (!failed.Contains("mech_thresholds"))
                {
                    mech = mech with
                    {
                        RechargeBelow = OptionalFact(v.RechargeBelow is not null, v.RechargeBelow.GetValueOrDefault()),
                        RechargeAbove = OptionalFact(v.RechargeAbove is not null, v.RechargeAbove.GetValueOrDefault()),
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
                    Deathresting = OptionalFact(v.Deathresting is not null, v.Deathresting.GetValueOrDefault()),
                    Level = OptionalFact(v.Level is not null, v.Level.GetValueOrDefault()),
                    LastDeathrestTick = OptionalInt(v.LastDeathrestTick is not null, v.LastDeathrestTick.GetValueOrDefault()),
                    DeathrestPercent = OptionalFact(v.DeathrestPercent is not null, v.DeathrestPercent.GetValueOrDefault()),
                    Capacity = OptionalInt(v.Capacity is not null, v.Capacity.GetValueOrDefault()),
                    BoundBuildings = OptionalInt(v.BoundBuildings is not null, v.BoundBuildings.GetValueOrDefault()),
                    AutoWake = OptionalFact(v.AutoWake is not null, v.AutoWake.GetValueOrDefault()),
                }
                : null;
            result = result with { Deathrest = Known(deathrest) };
        }
        if (!failed.Contains("gene_lifecycle"))
        {
            result = result with
            {
                XenogermRegrowTicksLeft = OptionalInt(biotech.XenogermRegrowTicksLeft is not null, biotech.XenogermRegrowTicksLeft.GetValueOrDefault()),
                XenogermComaTicksLeft = OptionalInt(biotech.XenogermComaTicksLeft is not null, biotech.XenogermComaTicksLeft.GetValueOrDefault()),
                InExtractor = OptionalFact(biotech.InExtractor is not null, biotech.InExtractor.GetValueOrDefault()),
            };
        }
        if (!failed.Contains("extractable") && biotech.Extractable is not null)
        {
            result = result with { Extractable = Known(biotech.Extractable.GetValueOrDefault()), ExtractableReason = Known((biotech.ExtractableReason ?? "")) };
        }
        return Known(result);
    }
}
