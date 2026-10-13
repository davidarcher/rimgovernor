using RimGovernor.Protocol.Observations;
using PolicyBuildingAnomaly = RimGovernor.Brain.Policy.BuildingAnomaly;
using PolicyContainmentDoor = RimGovernor.Brain.Policy.ContainmentDoor;
using PolicyContainmentMode = RimGovernor.Brain.Policy.ContainmentMode;
using PolicyCreepJoiner = RimGovernor.Brain.Policy.CreepJoiner;
using PolicyEntityHeld = RimGovernor.Brain.Policy.EntityHeld;
using PolicyEntityHolder = RimGovernor.Brain.Policy.EntityHolder;
using PolicyPawnAnomaly = RimGovernor.Brain.Policy.PawnAnomaly;
using PolicyStudyState = RimGovernor.Brain.Policy.StudyState;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go anomaly.go: the lifts of a pawn's and a building's Anomaly block. The block validators (validatePawnAnomaly,
// validateBuildingAnomaly, validateStudyState) belong to the pawn and building row decoders and are not ported.
public static partial class BridgeFunctions
{
    private static PolicyStudyState? StudyStateOf(StudyState? study) =>
        study is null
            ? null
            : new PolicyStudyState
            {
                StudyEnabled = OptionalFact(study.HasStudyEnabled, study.StudyEnabled),
                Completed = OptionalFact(study.HasCompleted, study.Completed),
                EverStudiable = OptionalFact(study.HasEverStudiable, study.EverStudiable),
                CurrentlyStudiable = OptionalFact(study.HasCurrentlyStudiable, study.CurrentlyStudiable),
                ProgressPercent = OptionalFact(study.HasProgressPercent, study.ProgressPercent),
                StudyPoints = OptionalFact(study.HasStudyPoints, study.StudyPoints),
                KnowledgeGained = OptionalFact(study.HasAnomalyKnowledgeGained, study.AnomalyKnowledgeGained),
                AnomalyKnowledge = OptionalFact(study.HasAnomalyKnowledge, study.AnomalyKnowledge),
                Interactions = OptionalInt(study.HasStudyInteractions, study.StudyInteractions),
                KnowledgeCategory = OptionalFact(study.HasKnowledgeCategory, study.KnowledgeCategory),
            };

    /// <summary>Go's <c>containmentMode</c>: an unmapped mode reads as the strictest, Execute.</summary>
    private static PolicyContainmentMode ContainmentModeOf(EntityContainmentModeKind mode) => mode switch
    {
        EntityContainmentModeKind.MaintainOnly => PolicyContainmentMode.MaintainOnly,
        EntityContainmentModeKind.Study => PolicyContainmentMode.Study,
        EntityContainmentModeKind.Release => PolicyContainmentMode.Release,
        _ => PolicyContainmentMode.Execute,
    };

    /// <summary>
    /// Go's <c>PawnAnomaly</c>: lifts a pawn row's Anomaly block; unknown when the row carries none (no Anomaly).
    /// A block a read issue names stays unknown.
    /// </summary>
    public static Fact<PolicyPawnAnomaly> PawnAnomaly(RimGovernor.Protocol.Observations.PawnAnomaly? anomaly)
    {
        if (anomaly is null)
        {
            return Unknown<PolicyPawnAnomaly>();
        }
        var failed = FailedFields(anomaly.Issues);
        var result = new PolicyPawnAnomaly
        {
            Entity = OptionalFact(anomaly.HasEntity, anomaly.Entity),
            Mutant = OptionalFact(anomaly.HasMutant, anomaly.Mutant),
            Shambler = OptionalFact(anomaly.HasShambler, anomaly.Shambler),
            MinContainmentStrength = OptionalFact(anomaly.HasMinContainmentStrength, anomaly.MinContainmentStrength),
            HiddenFromPlayer = OptionalFact(anomaly.HasHiddenFromPlayer, anomaly.HiddenFromPlayer),
            PsychicRitualInvoker = OptionalFact(anomaly.HasPsychicRitualInvoker, anomaly.PsychicRitualInvoker),
            MeleeOnly = OptionalFact(anomaly.HasMeleeOnly, anomaly.MeleeOnly),
            Held = Unknown<PolicyEntityHeld?>(),
            Study = Unknown<PolicyStudyState?>(),
            CreepJoiner = Unknown<PolicyCreepJoiner?>(),
        };
        if (!failed.Contains("creepjoiner"))
        {
            PolicyCreepJoiner? joiner = anomaly.Creepjoiner is { } c
                ? new PolicyCreepJoiner
                {
                    Form = OptionalFact(c.HasForm, c.Form),
                    Benefit = OptionalFact(c.HasBenefit, c.Benefit),
                    DownsideTriggered = OptionalFact(c.HasDownsideTriggered, c.DownsideTriggered),
                }
                : null;
            result = result with { CreepJoiner = Known(joiner) };
        }
        if (!failed.Contains("held"))
        {
            PolicyEntityHeld? held = null;
            if (anomaly.Held is { } h)
            {
                held = new PolicyEntityHeld
                {
                    Held = OptionalFact(h.HasHeld, h.Held),
                    Escaping = OptionalFact(h.HasEscaping, h.Escaping),
                    ExtractBioferrite = OptionalFact(h.HasExtractBioferrite, h.ExtractBioferrite),
                    NeedsTend = OptionalFact(h.HasNeedsTend, h.NeedsTend),
                    Bleeding = OptionalFact(h.HasBleeding, h.Bleeding),
                    HarvesterAttached = OptionalFact(h.HasHarvesterAttached, h.HarvesterAttached),
                    BioferritePerDay = OptionalFact(h.HasBioferritePerDay, h.BioferritePerDay),
                    CanBeCaptured = OptionalFact(h.HasCanBeCaptured, h.CanBeCaptured),
                    Platform = Known(h.Platform?.Id ?? ""),
                    Mode = h.HasMode ? Known(ContainmentModeOf(h.Mode)) : Unknown<PolicyContainmentMode>(),
                };
            }
            result = result with { Held = Known(held) };
        }
        if (!failed.Contains("study"))
        {
            result = result with { Study = Known(StudyStateOf(anomaly.Study)) };
        }
        return Known(result);
    }

    /// <summary>
    /// Go's <c>BuildingAnomaly</c>: lifts a building row's Anomaly block; unknown when the row carries none (no
    /// Anomaly). A block a read issue names stays unknown.
    /// </summary>
    public static Fact<PolicyBuildingAnomaly> BuildingAnomaly(BuildingState row)
    {
        if (row.Anomaly is not { } block)
        {
            return Unknown<PolicyBuildingAnomaly>();
        }
        var failed = FailedFields(block.Issues);
        var result = new PolicyBuildingAnomaly { Holder = Unknown<PolicyEntityHolder?>(), Study = Unknown<PolicyStudyState?>() };
        if (!failed.Contains("holder"))
        {
            PolicyEntityHolder? holder = null;
            if (block.Holder is { } h)
            {
                holder = new PolicyEntityHolder
                {
                    ContainmentStrength = OptionalFact(h.HasContainmentStrength, h.ContainmentStrength),
                    Available = OptionalFact(h.HasAvailable, h.Available),
                    HeldPawn = h.HeldPawn?.Id ?? "",
                    Doors = Unknown<EquatableArray<PolicyContainmentDoor>>(),
                };
                if (!failed.Contains("doors"))
                {
                    var doors = new List<PolicyContainmentDoor>(h.Doors.Count);
                    foreach (var door in h.Doors)
                    {
                        doors.Add(
                            new PolicyContainmentDoor
                            {
                                Cell = ToDomainCell(door.Cell),
                                Open = OptionalFact(door.HasOpen, door.Open),
                                HoldOpen = OptionalFact(door.HasHoldOpen, door.HoldOpen),
                                Breached = OptionalFact(door.HasContainmentBreached, door.ContainmentBreached),
                                BlockedOpen = OptionalFact(door.HasBlockedOpen, door.BlockedOpen),
                            });
                    }
                    holder = holder with { Doors = Known(EquatableArray.From(doors)) };
                }
            }
            result = result with { Holder = Known(holder) };
        }
        if (!failed.Contains("study"))
        {
            result = result with { Study = Known(StudyStateOf(block.Study)) };
        }
        return Known(result);
    }
}
