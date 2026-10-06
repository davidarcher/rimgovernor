package observation

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

func roundsWork(colony *o.ColonyFactsSnapshot, emergency policy.EmergencyFacts, snapshot *o.PawnSnapshot, catalog *bridge.DefinitionCatalog, things bridge.Things) (domain.Fact[[]policy.WorkPawn], error) {
	if colony == nil || colony.ColonistCount == nil || int(colony.GetColonistCount()) != len(emergency.Colonists) || len(snapshot.Pawns) != len(emergency.Colonists) {
		return domain.Unknown[[]policy.WorkPawn](), nil
	}
	byID := map[string]*o.PawnState{}
	for _, row := range snapshot.Pawns {
		byID[row.Pawn.GetId()] = row
	}
	var biotech *bridge.BiotechCatalog
	if catalog != nil {
		biotech = catalog.Biotech
	}
	rows := make([]policy.WorkPawn, 0, len(snapshot.Pawns))
	for _, p := range emergency.Colonists {
		row := byID[string(p.ID)]
		dead, dk := p.Dead.Value()
		downed, nk := p.Downed.Value()
		if row == nil || !dk || !nk || row.Dead == nil || row.Downed == nil || row.GetDead() != dead || row.GetDowned() != downed || row.Colonist == nil || !row.GetColonist() {
			return domain.Unknown[[]policy.WorkPawn](), nil
		}
		w, err := WorkPawnRow(row, catalog, things)
		if err != nil {
			return domain.Unknown[[]policy.WorkPawn](), err
		}
		if bt, ok := w.Biotech.Value(); ok {
			if child, known := bt.IsChild(); known && child {
				ages, err := biotech.WorkMinAges(row.Pawn.GetDefName())
				if err != nil {
					return domain.Unknown[[]policy.WorkPawn](), err
				}
				bt.WorkMinAges = domain.Known(ages)
				w.Biotech = domain.Known(bt)
			}
			if genes, known := bt.Genes.Value(); known {
				effects, err := biotech.GeneEffects(genes)
				if err != nil {
					return domain.Unknown[[]policy.WorkPawn](), err
				}
				bt.Effects = domain.Known(effects)
				w.Biotech = domain.Known(bt)
			}
		}
		rows = append(rows, w)
	}
	return domain.Known(rows), nil
}

// WorkPawnRow lifts one pawn row into the planner's WorkPawn: availability
// from the vital flags, the work snapshot token, manual mode, timetable and
// priorities from the settings block, skills, traits, incapable work types
// and age from the biography, and whether the primary weapon is ranged (its
// def rows, found through the frame's things table: unknown until the table
// holds the weapon). A missing or issued block leaves its facts unknown.
//
// The pawn's traits are resolved against the catalog (DefinitionCatalog.TraitEffects);
// a trait the catalog lacks is an error, and a pawn with traits needs a catalog.
func WorkPawnRow(row *o.PawnState, catalog *bridge.DefinitionCatalog, things bridge.Things) (policy.WorkPawn, error) {
	w := workPawnRow(row)
	// Native writes the Biotech block on every pawn when Biotech is active and
	// always names the developmental stage; a row without one is a contract
	// break, not an adult (#1784).
	if b := row.Biotech; b != nil && b.DevelopmentalStage == nil {
		return policy.WorkPawn{}, fmt.Errorf("pawn %s has a Biotech block without a developmental stage", row.Pawn.GetId())
	}
	if e := row.Equipment; e != nil {
		weapon, known, err := catalog.PrimaryWeapon(e, things)
		if err != nil {
			return policy.WorkPawn{}, err
		}
		if known {
			w.Ranged, w.Hunts = domain.Known(weapon.Ranged), domain.Known(weapon.Hunts())
		}
	}
	if rows, ok := w.Work.Value(); ok {
		for i := range rows {
			if err := catalog.ResolveWorkRow(&rows[i]); err != nil {
				return policy.WorkPawn{}, err
			}
		}
	}
	if traits, ok := w.Traits.Value(); ok {
		for i := range traits {
			if err := resolveTrait(catalog, &traits[i]); err != nil {
				return policy.WorkPawn{}, err
			}
		}
	}
	return w, nil
}

// resolveTrait sets the trait's Effects from the catalog.
func resolveTrait(catalog *bridge.DefinitionCatalog, trait *policy.PawnTrait) error {
	if catalog == nil {
		return fmt.Errorf("trait %s has no definition catalog to resolve against", trait.Name)
	}
	effects, err := catalog.TraitEffects(trait.Name, trait.Degree)
	if err != nil {
		return err
	}
	trait.Effects = effects
	return nil
}

func workPawnRow(row *o.PawnState) policy.WorkPawn {
	w := policy.WorkPawn{ID: policy.PawnID(row.Pawn.GetId())}
	mental, mk := nativePresence(row.MentalState, row.Issues, "mental_state").Value()
	if row.GetDead() || row.GetDowned() || row.GetDrafted() || mk && mental {
		w.Available = domain.Known(false)
	} else if row.Dead != nil && row.Downed != nil && row.Drafted != nil && mk {
		w.Available = domain.Known(true)
	}
	if s := row.Settings; s != nil {
		if food := s.FoodRestriction; food != nil && food.PolicyId != nil && !hasIssue(s.Issues, "food_restriction") {
			w.FoodRestriction = domain.Known(policy.FoodRestriction{PolicyID: food.GetPolicyId(), Allowed: append([]string(nil), food.AllowedDefs...)})
		}
		w.PolicyInputs = bridge.PawnPolicyInputs(s.PolicyInputs)
		if s.MedicalCare != nil && !hasIssue(s.Issues, "medical_care") {
			w.MedicalCare = domain.Known(bridge.MedicalCareName(s.GetMedicalCare()))
		}
		w.Applies = optional(s.WorkApplies)
		w.Manual = optional(s.ManualWorkPriorities)
		if !hasIssue(s.Issues, "schedule") && len(s.Schedule) == 24 {
			slots := make([]string, 24)
			known := true
			seen := make([]bool, 24)
			for _, slot := range s.Schedule {
				if slot.Hour == nil || slot.AssignmentDefName == nil || slot.GetHour() >= 24 || seen[slot.GetHour()] || slot.GetAssignmentDefName() == "" {
					known = false
					break
				}
				seen[slot.GetHour()] = true
				slots[slot.GetHour()] = slot.GetAssignmentDefName()
			}
			if known {
				w.Schedule = domain.Known(slots)
			}
		}
		if !hasIssue(s.Issues, "work") {
			work := []policy.WorkPriority{}
			known := true
			for _, entry := range s.Work {
				if entry.Priority == nil || entry.Disabled == nil {
					known = false
					break
				}
				work = append(work, policy.WorkPriority{Work: policy.WorkType(entry.GetDefName()), Priority: int(entry.GetPriority()), Disabled: entry.GetDisabled()})
			}
			if known {
				w.Work = domain.Known(work)
			}
		}
	}
	if j := row.Job; j != nil && !hasIssue(row.Issues, "job") {
		// JobRow: a pawn with no job carries the current_job issue and no
		// def; a job always carries its def and, when a work giver issued
		// it, that giver's work type.
		switch {
		case hasIssue(j.Issues, "current_job") && j.DefName == nil:
			w.Job = domain.Known(policy.PawnJob{})
		case !hasIssue(j.Issues, "current_job") && j.DefName != nil:
			w.Job = domain.Known(policy.PawnJob{Def: j.GetDefName(), Work: policy.WorkType(j.GetWorkTypeDefName()), Target: jobTarget(j.TargetA, j.TargetACell)})
		}
	}
	if b := row.Biography; b != nil {
		if !hasIssue(b.Issues, "skills") {
			skills := []policy.WorkSkill{}
			known := true
			for _, entry := range b.Skills {
				if entry.Level == nil || entry.Disabled == nil || entry.Passion == nil {
					known = false
					break
				}
				skills = append(skills, policy.WorkSkill{Name: entry.GetDefName(), Level: int(entry.GetLevel()), Stored: int(entry.GetStoredLevel()), Disabled: entry.GetDisabled(), Passion: skillPassion(entry.GetPassion())})
			}
			if known {
				w.Skills = domain.Known(skills)
			}
		}
		// Traits, incapable work types and age feed the pawn profile
		// (policy.BuildProfile); a read issue on them leaves the row
		// unknown and the planner skill-only for this pawn.
		if !hasIssue(b.Issues, "traits") {
			traits := []policy.PawnTrait{}
			for _, entry := range b.Traits {
				traits = append(traits, policy.PawnTrait{Name: entry.GetDefName(), Degree: int(entry.GetDegree())})
			}
			w.Traits = domain.Known(traits)
		}
		incapable := []policy.WorkType{}
		for _, name := range b.IncapableWorkTypes {
			incapable = append(incapable, policy.WorkType(name))
		}
		w.Incapable = domain.Known(incapable)
		w.Age = optional(b.BiologicalAgeYears)
	}
	w.Inspiration = optional(row.Inspiration)
	w.Biotech = bridge.PawnBiotech(row.Biotech)
	if bt, ok := w.Biotech.Value(); ok && bt.IsDeathresting() {
		w.Available = domain.Known(false)
	}
	if s := row.Settings; s != nil && s.HostilityResponse != nil && !hasIssue(s.Issues, "hostility_response") {
		w.Hostility = domain.Known(bridge.HostilityName(s.GetHostilityResponse()))
	}
	if s := row.Settings; s != nil && s.SelfTend != nil && !hasIssue(s.Issues, "self_tend") {
		w.SelfTend = domain.Known(s.GetSelfTend())
	}
	if b := row.Biography; b != nil && !hasIssue(b.Issues, "disabled_work_tags") {
		violent := true
		for _, tag := range b.DisabledWorkTags {
			violent = violent && tag != "Violent"
		}
		w.ViolenceCapable = domain.Known(violent)
	}
	if h := row.Health; h != nil {
		w.BloodLoss = optional(h.BloodLoss)
		w.Health = optional(h.SummaryFraction)
	}
	if n := row.Needs; n != nil && !hasIssue(row.Issues, "needs") {
		w.Rest, w.Joy, w.Mood = optional(n.Rest), optional(n.Joy), optional(n.Mood)
		w.BreakThreshold = optional(n.BreakThresholdMinor)
	}
	if s := row.Social; s != nil && !hasIssue(row.Issues, "social") {
		w.HighExpectations = optional(s.HighExpectations)
	}
	if n := row.Needs; n != nil && n.Psyfocus != nil && n.PsyfocusTarget != nil && n.PsylinkLevel != nil {
		w.Psyfocus, w.PsyfocusTarget, w.PsylinkLevel = domain.Known(n.GetPsyfocus()), domain.Known(n.GetPsyfocusTarget()), domain.Known(int(n.GetPsylinkLevel()))
	}
	return w
}

// jobTarget reads a job's targetA (#643). An absent field is an older
// producer (unknown); an unavailable target is a job with none.
func jobTarget(t *o.TargetRef, at *c.Cell) domain.Fact[policy.JobTarget] {
	switch {
	case t == nil:
		return domain.Unknown[policy.JobTarget]()
	case t.GetEntity() != nil:
		e := t.GetEntity()
		target := policy.JobTarget{Thing: e.GetId()}
		if at != nil && at.X != nil && at.Z != nil {
			target.Cell = domain.Known(domain.Cell{X: at.GetX(), Z: at.GetZ()})
		}
		if _, cell := target.Cell.Value(); target.Thing == "" && !cell {
			return domain.Unknown[policy.JobTarget]()
		}
		return domain.Known(target)
	case t.GetCell() != nil && t.GetCell().X != nil && t.GetCell().Z != nil:
		return domain.Known(policy.JobTarget{Cell: domain.Known(domain.Cell{X: t.GetCell().GetX(), Z: t.GetCell().GetZ()})})
	case t.GetUnavailable() != nil:
		return domain.Known(policy.JobTarget{})
	}
	return domain.Unknown[policy.JobTarget]()
}

// skillPassion normalises native Passion.ToString(): a pawn without passion
// sends "None", which policy must read as "" (Passion != "" means passionate).
// skillPassion is the passion name policy reads; no passion is "".
func skillPassion(p o.Passion) string {
	if p == o.Passion_PASSION_NONE {
		return ""
	}
	return bridge.PassionName(p)
}

// careName is the MedicalCareCategory name of an optional wire care tier.
func careName(care *op.MedicalCare) domain.Fact[string] {
	if care == nil {
		return domain.Unknown[string]()
	}
	return domain.Known(bridge.MedicalCareName(*care))
}
