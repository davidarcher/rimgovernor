using RimGovernor.Contracts.Wire.Observations;
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
                StudyEnabled = OptionalFact(study.StudyEnabled is not null, study.StudyEnabled.GetValueOrDefault()),
                Completed = OptionalFact(study.Completed is not null, study.Completed.GetValueOrDefault()),
                EverStudiable = OptionalFact(study.EverStudiable is not null, study.EverStudiable.GetValueOrDefault()),
                CurrentlyStudiable = OptionalFact(study.CurrentlyStudiable is not null, study.CurrentlyStudiable.GetValueOrDefault()),
                ProgressPercent = OptionalFact(study.ProgressPercent is not null, study.ProgressPercent.GetValueOrDefault()),
                StudyPoints = OptionalFact(study.StudyPoints is not null, study.StudyPoints.GetValueOrDefault()),
                KnowledgeGained = OptionalFact(study.AnomalyKnowledgeGained is not null, study.AnomalyKnowledgeGained.GetValueOrDefault()),
                AnomalyKnowledge = OptionalFact(study.AnomalyKnowledge is not null, study.AnomalyKnowledge.GetValueOrDefault()),
                Interactions = OptionalInt(study.StudyInteractions is not null, study.StudyInteractions.GetValueOrDefault()),
                KnowledgeCategory = OptionalFact(study.KnowledgeCategory is not null, (study.KnowledgeCategory ?? "")),
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
    public static Fact<PolicyPawnAnomaly> PawnAnomaly(RimGovernor.Contracts.Wire.Observations.PawnAnomaly? anomaly)
    {
        if (anomaly is null)
        {
            return Unknown<PolicyPawnAnomaly>();
        }
        var failed = FailedFields(anomaly.Issues);
        var result = new PolicyPawnAnomaly
        {
            Entity = OptionalFact(anomaly.Entity is not null, anomaly.Entity.GetValueOrDefault()),
            Mutant = OptionalFact(anomaly.Mutant is not null, anomaly.Mutant.GetValueOrDefault()),
            Shambler = OptionalFact(anomaly.Shambler is not null, anomaly.Shambler.GetValueOrDefault()),
            MinContainmentStrength = OptionalFact(anomaly.MinContainmentStrength is not null, anomaly.MinContainmentStrength.GetValueOrDefault()),
            HiddenFromPlayer = OptionalFact(anomaly.HiddenFromPlayer is not null, anomaly.HiddenFromPlayer.GetValueOrDefault()),
            PsychicRitualInvoker = OptionalFact(anomaly.PsychicRitualInvoker is not null, anomaly.PsychicRitualInvoker.GetValueOrDefault()),
            MeleeOnly = OptionalFact(anomaly.MeleeOnly is not null, anomaly.MeleeOnly.GetValueOrDefault()),
            Held = Unknown<PolicyEntityHeld?>(),
            Study = Unknown<PolicyStudyState?>(),
            CreepJoiner = Unknown<PolicyCreepJoiner?>(),
        };
        if (!failed.Contains("creepjoiner"))
        {
            PolicyCreepJoiner? joiner = anomaly.Creepjoiner is { } c
                ? new PolicyCreepJoiner
                {
                    Form = OptionalFact(c.Form is not null, (c.Form ?? "")),
                    Benefit = OptionalFact(c.Benefit is not null, (c.Benefit ?? "")),
                    DownsideTriggered = OptionalFact(c.DownsideTriggered is not null, c.DownsideTriggered.GetValueOrDefault()),
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
                    Held = OptionalFact(h.Held is not null, h.Held.GetValueOrDefault()),
                    Escaping = OptionalFact(h.Escaping is not null, h.Escaping.GetValueOrDefault()),
                    ExtractBioferrite = OptionalFact(h.ExtractBioferrite is not null, h.ExtractBioferrite.GetValueOrDefault()),
                    NeedsTend = OptionalFact(h.NeedsTend is not null, h.NeedsTend.GetValueOrDefault()),
                    Bleeding = OptionalFact(h.Bleeding is not null, h.Bleeding.GetValueOrDefault()),
                    HarvesterAttached = OptionalFact(h.HarvesterAttached is not null, h.HarvesterAttached.GetValueOrDefault()),
                    BioferritePerDay = OptionalFact(h.BioferritePerDay is not null, h.BioferritePerDay.GetValueOrDefault()),
                    CanBeCaptured = OptionalFact(h.CanBeCaptured is not null, h.CanBeCaptured.GetValueOrDefault()),
                    Platform = Known(h.Platform?.Id ?? ""),
                    Mode = h.Mode is not null ? Known(ContainmentModeOf(h.Mode.GetValueOrDefault())) : Unknown<PolicyContainmentMode>(),
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
                    ContainmentStrength = OptionalFact(h.ContainmentStrength is not null, h.ContainmentStrength.GetValueOrDefault()),
                    Available = OptionalFact(h.Available is not null, h.Available.GetValueOrDefault()),
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
                                Open = OptionalFact(door.Open is not null, door.Open.GetValueOrDefault()),
                                HoldOpen = OptionalFact(door.HoldOpen is not null, door.HoldOpen.GetValueOrDefault()),
                                Breached = OptionalFact(door.ContainmentBreached is not null, door.ContainmentBreached.GetValueOrDefault()),
                                BlockedOpen = OptionalFact(door.BlockedOpen is not null, door.BlockedOpen.GetValueOrDefault()),
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
