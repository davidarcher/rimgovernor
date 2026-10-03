package bridge

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// AnomalyCatalog is one load's Anomaly defs (#1737) by name, the native rows
// as read: what an entity needs, what a study yields and which incidents
// are Anomaly threats are the game defs' own, never Go name lists. Nil
// without Anomaly. Thing rows are the ThingDefs that are entities, are
// studiable, can be held or hold an entity; incident rows are the incidents
// the game marks as Anomaly incidents.
type AnomalyCatalog struct {
	EntityCategories    map[string]*o.EntityCategoryRow
	KnowledgeCategories map[string]*o.KnowledgeCategoryRow
	Codex               map[string]*o.EntityCodexRow
	Things              map[string]*o.AnomalyThingRow
	Incidents           map[string]*o.AnomalyIncidentRow
}

// DecodeAnomalyCatalog validates the catalog's Anomaly section; nil in, nil
// out (the game has no Anomaly).
func DecodeAnomalyCatalog(v *o.AnomalyCatalog) (*AnomalyCatalog, error) {
	if v == nil {
		return nil, nil
	}
	out := &AnomalyCatalog{}
	var err error
	if out.EntityCategories, err = catalogIndex("anomaly entity category", v.EntityCategories, (*o.EntityCategoryRow).GetDefName); err != nil {
		return nil, err
	}
	if out.KnowledgeCategories, err = catalogIndex("anomaly knowledge category", v.KnowledgeCategories, (*o.KnowledgeCategoryRow).GetDefName); err != nil {
		return nil, err
	}
	if out.Codex, err = catalogIndex("anomaly codex entry", v.CodexEntries, (*o.EntityCodexRow).GetDefName); err != nil {
		return nil, err
	}
	if out.Things, err = catalogIndex("anomaly thing", v.Things, (*o.AnomalyThingRow).GetDefName); err != nil {
		return nil, err
	}
	if out.Incidents, err = catalogIndex("anomaly incident", v.Incidents, (*o.AnomalyIncidentRow).GetDefName); err != nil {
		return nil, err
	}
	knowledge := func(owner, name string) error {
		if out.KnowledgeCategories[name] == nil {
			return contract("anomaly %s names unknown knowledge category %s", owner, name)
		}
		return nil
	}
	codex := func(owner, name string) error {
		if out.Codex[name] == nil {
			return contract("anomaly %s names unknown codex entry %s", owner, name)
		}
		return nil
	}
	for _, row := range v.KnowledgeCategories {
		if row.OverflowCategory != nil {
			if err := knowledge("knowledge category "+row.GetDefName(), row.GetOverflowCategory()); err != nil {
				return nil, err
			}
		}
	}
	for _, row := range v.CodexEntries {
		if row.Category != nil && out.EntityCategories[row.GetCategory()] == nil {
			return nil, contract("anomaly codex entry %s names unknown entity category %s", row.GetDefName(), row.GetCategory())
		}
		if row.DiscoveryType != nil && (row.GetDiscoveryType() == o.EntityDiscoveryKind_ENTITY_DISCOVERY_KIND_UNSPECIFIED || o.EntityDiscoveryKind_name[int32(row.GetDiscoveryType())] == "") {
			return nil, contract("anomaly codex entry %s has an unknown discovery type", row.GetDefName())
		}
		if err := catalogIDs("anomaly codex entry", row.LinkedThings, row.ProvocationIncidents, row.DiscoveredResearchProjects); err != nil {
			return nil, err
		}
	}
	for _, row := range v.Things {
		if row.CodexEntry != nil {
			if err := codex("thing "+row.GetDefName(), row.GetCodexEntry()); err != nil {
				return nil, err
			}
		}
		if row.RaceKnowledgeCategory != nil {
			if err := knowledge("thing "+row.GetDefName(), row.GetRaceKnowledgeCategory()); err != nil {
				return nil, err
			}
		}
		if err := catalogNumbers("anomaly thing", row.MinContainmentStrength); err != nil {
			return nil, err
		}
		if s := row.Studiable; s != nil {
			if err := catalogNumbers("anomaly studiable", s.StudyAmountToComplete, s.AnomalyKnowledge, s.KnowledgeFactorOutdoors); err != nil {
				return nil, err
			}
			if s.Comp != nil && validID(s.GetComp()) != nil {
				return nil, contract("invalid anomaly studiable comp")
			}
			if s.KnowledgeCategory != nil {
				if err := knowledge("thing "+row.GetDefName(), s.GetKnowledgeCategory()); err != nil {
					return nil, err
				}
			}
		}
		if t := row.HoldingTarget; t != nil {
			if err := catalogNumbers("anomaly holding target", t.BaseEscapeIntervalMtbDays); err != nil {
				return nil, err
			}
			if t.HeldPawnKind != nil && validID(t.GetHeldPawnKind()) != nil {
				return nil, contract("invalid anomaly holding target pawn kind")
			}
		}
		if h := row.Holder; h != nil {
			if err := catalogNumbers("anomaly holder", h.ContainmentFactor); err != nil {
				return nil, err
			}
			if h.Comp != nil && validID(h.GetComp()) != nil {
				return nil, contract("invalid anomaly holder comp")
			}
		}
	}
	for _, row := range v.Incidents {
		if err := catalogNumbers("anomaly incident", row.BaseChance, row.MinThreatPoints); err != nil {
			return nil, err
		}
		for _, id := range []*string{row.Category, row.Worker} {
			if id != nil && validID(*id) != nil {
				return nil, contract("invalid anomaly incident %s name", row.GetDefName())
			}
		}
		if err := catalogIDs("anomaly incident", row.TargetTags); err != nil {
			return nil, err
		}
		if row.CodexEntry != nil {
			if err := codex("incident "+row.GetDefName(), row.GetCodexEntry()); err != nil {
				return nil, err
			}
		}
	}
	return out, nil
}

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

// validatePawnAnomaly bounds a pawn row's Anomaly block (#1737).
func validatePawnAnomaly(a *o.PawnAnomaly) error {
	if a == nil {
		return nil
	}
	if err := catalogNumbers("anomaly pawn", a.MinContainmentStrength); err != nil {
		return err
	}
	if h := a.Held; h != nil {
		if h.Platform != nil && !validRef(h.Platform) {
			return contract("invalid anomaly holding platform")
		}
		if h.Mode != nil && (h.GetMode() == o.EntityContainmentModeKind_ENTITY_CONTAINMENT_MODE_KIND_UNSPECIFIED || o.EntityContainmentModeKind_name[int32(h.GetMode())] == "") {
			return contract("anomaly pawn has an unknown containment mode")
		}
	}
	if err := validateStudyState(a.Study); err != nil {
		return err
	}
	return pawnsIssues(a.Issues, a.ProtoReflect())
}

// validateBuildingAnomaly bounds a building row's Anomaly block (#1737).
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
		HiddenFromPlayer:       optionalFact(a.HiddenFromPlayer), PsychicRitualInvoker: optionalFact(a.PsychicRitualInvoker), MeleeOnly: optionalFact(a.MeleeOnly), Held: domain.Unknown[*policy.EntityHeld](), Study: domain.Unknown[*policy.StudyState]()}
	if !failed["held"] {
		var held *policy.EntityHeld
		if h := a.Held; h != nil {
			held = &policy.EntityHeld{Held: optionalFact(h.Held), Escaping: optionalFact(h.Escaping), ExtractBioferrite: optionalFact(h.ExtractBioferrite),
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
			holder = &policy.EntityHolder{ContainmentStrength: optionalFact(h.ContainmentStrength), Available: optionalFact(h.Available), HeldPawn: h.GetHeldPawn().GetId()}
		}
		r.Holder = domain.Known(holder)
	}
	if !failed["study"] {
		r.Study = domain.Known(studyState(b.Study))
	}
	return domain.Known(r)
}
