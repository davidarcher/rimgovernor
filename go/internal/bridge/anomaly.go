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
	// Creepjoiner parts (#1740) by def name. The downside rows are the
	// static defs; which downside a given pawn has is hidden and is not in
	// any pawn row.
	CreepJoinerForms     map[string]*o.CreepJoinerFormRow
	CreepJoinerBenefits  map[string]*o.CreepJoinerBenefitRow
	CreepJoinerDownsides map[string]*o.CreepJoinerDownsideRow
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
	if err = decodeCreepJoinerCatalog(v, out); err != nil {
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

// decodeCreepJoinerCatalog indexes the creepjoiner defs (#1740). Numbers are
// the def values and only checked for being finite; requires and excludes
// name other creepjoiner defs (aggressive and rejection defs among them,
// which the catalog does not carry), so they are checked for valid names only.
func decodeCreepJoinerCatalog(v *o.AnomalyCatalog, out *AnomalyCatalog) (err error) {
	if out.CreepJoinerForms, err = catalogIndex("creepjoiner form", v.CreepjoinerForms, (*o.CreepJoinerFormRow).GetDefName); err != nil {
		return err
	}
	if out.CreepJoinerBenefits, err = catalogIndex("creepjoiner benefit", v.CreepjoinerBenefits, (*o.CreepJoinerBenefitRow).GetDefName); err != nil {
		return err
	}
	if out.CreepJoinerDownsides, err = catalogIndex("creepjoiner downside", v.CreepjoinerDownsides, (*o.CreepJoinerDownsideRow).GetDefName); err != nil {
		return err
	}
	for _, row := range v.CreepjoinerForms {
		if err = catalogNumbers("creepjoiner form", row.Weight, row.MinCombatPoints); err == nil {
			err = catalogIDs("creepjoiner form", row.Requires, row.Excludes)
		}
		if err != nil {
			return err
		}
	}
	for _, row := range v.CreepjoinerBenefits {
		if err = catalogNumbers("creepjoiner benefit", row.Weight, row.MinCombatPoints); err == nil {
			err = catalogIDs("creepjoiner benefit", row.Requires, row.Excludes, row.Traits, row.Hediffs, row.Abilities)
		}
		if err != nil {
			return err
		}
		for _, skill := range row.Skills {
			if validID(skill.GetSkill()) != nil || skill.Min == nil || skill.Max == nil || skill.GetMin() > skill.GetMax() {
				return contract("invalid creepjoiner benefit %s skill", row.GetDefName())
			}
		}
	}
	for _, row := range v.CreepjoinerDownsides {
		err = catalogNumbers("creepjoiner downside", row.Weight, row.MinCombatPoints, row.TriggersAfterDaysMin, row.TriggersAfterDaysMax,
			row.TriggerMtbDays, row.TriggerMinDaysMin, row.TriggerMinDaysMax)
		if err == nil {
			err = catalogIDs("creepjoiner downside", row.Requires, row.Excludes, row.Traits, row.Hediffs, row.Abilities)
		}
		if err != nil {
			return err
		}
		if row.Worker != nil && validID(row.GetWorker()) != nil {
			return contract("invalid creepjoiner downside %s worker", row.GetDefName())
		}
	}
	return nil
}
