package bridge

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// validateStudyState bounds a CompStudiable block of a pawn or building row.
func validateStudyState(s *o.StudyState) error {
	if s == nil {
		return nil
	}
	if err := catalogNumbers("anomaly study", s.ProgressPercent, s.StudyPoints, s.AnomalyKnowledgeGained, s.AnomalyKnowledge); err != nil {
		return err
	}
	if s.ProgressPercent != nil && (s.GetProgressPercent() < 0 || s.GetProgressPercent() > 1) {
		return contract("anomaly study progress outside 0..1")
	}
	if s.StudyPoints != nil && s.GetStudyPoints() < 0 || s.AnomalyKnowledgeGained != nil && s.GetAnomalyKnowledgeGained() < 0 ||
		s.AnomalyKnowledge != nil && s.GetAnomalyKnowledge() < 0 || s.StudyInteractions != nil && s.GetStudyInteractions() < 0 {
		return contract("negative anomaly study number")
	}
	if s.KnowledgeCategory != nil && validID(s.GetKnowledgeCategory()) != nil {
		return contract("invalid anomaly study knowledge category")
	}
	return nil
}

// validatePawnAnomaly bounds a pawn row's Anomaly block.
func validatePawnAnomaly(a *o.PawnAnomaly) error {
	if a == nil {
		return nil
	}
	if err := catalogNumbers("anomaly pawn", a.MinContainmentStrength); err != nil {
		return err
	}
	if h := a.Held; h != nil {
		if err := catalogNumbers("anomaly held", h.BioferritePerDay); err != nil {
			return err
		}
		if h.Platform != nil && !validRef(h.Platform) {
			return contract("invalid anomaly holding platform")
		}
		if h.Mode != nil && (h.GetMode() == o.EntityContainmentModeKind_ENTITY_CONTAINMENT_MODE_KIND_UNSPECIFIED || o.EntityContainmentModeKind_name[int32(h.GetMode())] == "") {
			return contract("anomaly pawn has an unknown containment mode")
		}
	}
	if c := a.Creepjoiner; c != nil {
		for _, id := range []*string{c.Form, c.Benefit} {
			if id != nil && validID(*id) != nil {
				return contract("invalid creepjoiner def name")
			}
		}
	}
	if err := validateStudyState(a.Study); err != nil {
		return err
	}
	return pawnsIssues(a.Issues, a.ProtoReflect())
}

// validateBuildingAnomaly bounds a building row's Anomaly block.
func validateBuildingAnomaly(b *o.AnomalyBuilding) error {
	if b == nil {
		return nil
	}
	if h := b.Holder; h != nil {
		if h.ContainmentStrength != nil && (math.IsNaN(h.GetContainmentStrength()) || math.IsInf(h.GetContainmentStrength(), 0) || h.GetContainmentStrength() < 0) {
			return contract("invalid anomaly holder containment strength")
		}
		if h.HeldPawn != nil && !validRef(h.HeldPawn) {
			return contract("invalid anomaly held pawn")
		}
		for _, door := range h.Doors {
			if err := validCell(door.GetCell()); err != nil {
				return contract("anomaly holder door: %v", err)
			}
		}
	}
	if err := validateStudyState(b.Study); err != nil {
		return err
	}
	return pawnsIssues(b.Issues, b.ProtoReflect())
}

func studyState(s *o.StudyState) *policy.StudyState {
	if s == nil {
		return nil
	}
	return &policy.StudyState{StudyEnabled: optionalFact(s.StudyEnabled), Completed: optionalFact(s.Completed), EverStudiable: optionalFact(s.EverStudiable),
		CurrentlyStudiable: optionalFact(s.CurrentlyStudiable), ProgressPercent: optionalFact(s.ProgressPercent), StudyPoints: optionalFact(s.StudyPoints),
		KnowledgeGained: optionalFact(s.AnomalyKnowledgeGained), AnomalyKnowledge: optionalFact(s.AnomalyKnowledge), Interactions: optionalInt(s.StudyInteractions),
		KnowledgeCategory: optionalFact(s.KnowledgeCategory)}
}

func failedFields(issues []*o.ReadIssue) map[string]bool {
	failed := map[string]bool{}
	for _, issue := range issues {
		failed[issue.GetField()] = true
	}
	return failed
}

// PawnAnomaly lifts a pawn row's Anomaly block; unknown when the row carries
// none (no Anomaly). A block a read issue names stays unknown.
func PawnAnomaly(a *o.PawnAnomaly) domain.Fact[policy.PawnAnomaly] {
	if a == nil {
		return domain.Unknown[policy.PawnAnomaly]()
	}
	failed := failedFields(a.Issues)
	r := policy.PawnAnomaly{Entity: optionalFact(a.Entity), Mutant: optionalFact(a.Mutant), Shambler: optionalFact(a.Shambler),
		MinContainmentStrength: optionalFact(a.MinContainmentStrength),
		HiddenFromPlayer:       optionalFact(a.HiddenFromPlayer), PsychicRitualInvoker: optionalFact(a.PsychicRitualInvoker), MeleeOnly: optionalFact(a.MeleeOnly), Held: domain.Unknown[*policy.EntityHeld](), Study: domain.Unknown[*policy.StudyState](),
		CreepJoiner: domain.Unknown[*policy.CreepJoiner]()}
	if !failed["creepjoiner"] {
		var joiner *policy.CreepJoiner
		if c := a.Creepjoiner; c != nil {
			joiner = &policy.CreepJoiner{Form: optionalFact(c.Form), Benefit: optionalFact(c.Benefit), DownsideTriggered: optionalFact(c.DownsideTriggered)}
		}
		r.CreepJoiner = domain.Known(joiner)
	}
	if !failed["held"] {
		var held *policy.EntityHeld
		if h := a.Held; h != nil {
			held = &policy.EntityHeld{Held: optionalFact(h.Held), Escaping: optionalFact(h.Escaping), ExtractBioferrite: optionalFact(h.ExtractBioferrite),
				NeedsTend: optionalFact(h.NeedsTend), Bleeding: optionalFact(h.Bleeding),
				HarvesterAttached: optionalFact(h.HarvesterAttached), BioferritePerDay: optionalFact(h.BioferritePerDay),
				CanBeCaptured: optionalFact(h.CanBeCaptured), Platform: domain.Known(h.GetPlatform().GetId()), Mode: domain.Unknown[policy.ContainmentMode]()}
			if h.Mode != nil {
				held.Mode = domain.Known(containmentMode(h.GetMode()))
			}
		}
		r.Held = domain.Known(held)
	}
	if !failed["study"] {
		r.Study = domain.Known(studyState(a.Study))
	}
	return domain.Known(r)
}

func containmentMode(m o.EntityContainmentModeKind) policy.ContainmentMode {
	switch m {
	case o.EntityContainmentModeKind_ENTITY_CONTAINMENT_MODE_KIND_MAINTAIN_ONLY:
		return policy.ContainmentMaintainOnly
	case o.EntityContainmentModeKind_ENTITY_CONTAINMENT_MODE_KIND_STUDY:
		return policy.ContainmentStudy
	case o.EntityContainmentModeKind_ENTITY_CONTAINMENT_MODE_KIND_RELEASE:
		return policy.ContainmentRelease
	default:
		return policy.ContainmentExecute
	}
}

// BuildingAnomaly lifts a building row's Anomaly block; unknown when the row
// carries none (no Anomaly). A block a read issue names stays unknown.
func BuildingAnomaly(row *o.BuildingState) domain.Fact[policy.BuildingAnomaly] {
	b := row.GetAnomaly()
	if b == nil {
		return domain.Unknown[policy.BuildingAnomaly]()
	}
	failed := failedFields(b.Issues)
	r := policy.BuildingAnomaly{Holder: domain.Unknown[*policy.EntityHolder](), Study: domain.Unknown[*policy.StudyState]()}
	if !failed["holder"] {
		var holder *policy.EntityHolder
		if h := b.Holder; h != nil {
			holder = &policy.EntityHolder{ContainmentStrength: optionalFact(h.ContainmentStrength), Available: optionalFact(h.Available), HeldPawn: h.GetHeldPawn().GetId(), Doors: domain.Unknown[[]policy.ContainmentDoor]()}
			if !failed["doors"] {
				doors := make([]policy.ContainmentDoor, 0, len(h.Doors))
				for _, d := range h.Doors {
					doors = append(doors, policy.ContainmentDoor{Cell: domain.Cell{X: d.GetCell().GetX(), Z: d.GetCell().GetZ()}, Open: optionalFact(d.Open), HoldOpen: optionalFact(d.HoldOpen),
						Breached: optionalFact(d.ContainmentBreached), BlockedOpen: optionalFact(d.BlockedOpen)})
				}
				holder.Doors = domain.Known(doors)
			}
		}
		r.Holder = domain.Known(holder)
	}
	if !failed["study"] {
		r.Study = domain.Known(studyState(b.Study))
	}
	return domain.Known(r)
}
