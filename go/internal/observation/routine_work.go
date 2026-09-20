package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func routineWork(colony *o.ColonyFactsSnapshot, emergency policy.EmergencyFacts, snapshot *o.PawnSnapshot) domain.Fact[[]policy.WorkPawn] {
	if colony == nil || colony.ColonistCount == nil || int(colony.GetColonistCount()) != len(emergency.Colonists) || len(snapshot.Pawns) != len(emergency.Colonists) {
		return domain.Unknown[[]policy.WorkPawn]()
	}
	byID := map[string]*o.PawnState{}
	for _, row := range snapshot.Pawns {
		byID[row.Pawn.GetId()] = row
	}
	rows := make([]policy.WorkPawn, 0, len(snapshot.Pawns))
	for _, p := range emergency.Colonists {
		row := byID[string(p.ID)]
		dead, dk := p.Dead.Value()
		downed, nk := p.Downed.Value()
		if row == nil || !dk || !nk || row.Dead == nil || row.Downed == nil || row.GetDead() != dead || row.GetDowned() != downed || row.Colonist == nil || !row.GetColonist() {
			return domain.Unknown[[]policy.WorkPawn]()
		}
		w := WorkPawnRow(row)
		rows = append(rows, w)
	}
	return domain.Known(rows)
}

// WorkPawnRow lifts one pawn row into the planner's WorkPawn: availability
// from the vital flags, the work snapshot token, manual mode, timetable and
// priorities from the settings block, skills, traits, incapable work types
// and age from the biography, and whether the primary weapon is ranged. A
// missing or issued block leaves its facts unknown.
func WorkPawnRow(row *o.PawnState) policy.WorkPawn {
	w := policy.WorkPawn{ID: policy.PawnID(row.Pawn.GetId())}
	mental, mk := nativePresence(row.MentalState, row.Issues, "mental_state").Value()
	if row.GetDead() || row.GetDowned() || row.GetDrafted() || mk && mental {
		w.Available = domain.Known(false)
	} else if row.Dead != nil && row.Downed != nil && row.Drafted != nil && mk {
		w.Available = domain.Known(true)
	}
	if s := row.Settings; s != nil {
		if food := s.FoodRestriction; food != nil && food.PolicyId != nil && !hasIssue(s.Issues, "food_restriction") {
			w.FoodRestriction = domain.Known(policy.FoodRestriction{PolicyID: food.GetPolicyId(), Allowed: append([]string(nil), food.AllowedDefs...), Eligible: append([]string(nil), food.EligibleDefs...)})
		}
		if s.DrugPolicyWritable != nil && s.DrugPolicyName != nil && s.DrugPolicyDefault != nil {
			w.DrugPolicyWritable = domain.Known(s.GetDrugPolicyWritable())
			w.DrugPolicyDefault = domain.Known(s.GetDrugPolicyDefault())
			w.DrugPolicyName = s.GetDrugPolicyName()
		}
		if s.Snapshot != nil {
			w.SnapshotToken = domain.Known(s.Snapshot.GetToken())
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
			w.Job = domain.Known(policy.PawnJob{Def: j.GetDefName(), Work: policy.WorkType(j.GetWorkTypeDefName())})
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
				skills = append(skills, policy.WorkSkill{Name: entry.Definition.GetDefName(), Level: int(entry.GetLevel()), Stored: int(entry.GetStoredLevel()), Disabled: entry.GetDisabled(), Passion: entry.GetPassion()})
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
	if e := row.Equipment; e != nil {
		if e.Armed != nil && !e.GetArmed() {
			w.Ranged = domain.Known(false)
		} else {
			for _, gear := range e.Equipped {
				if gear.Thing.GetId() == e.GetPrimaryId() {
					w.Ranged = optional(gear.Ranged)
				}
			}
		}
	}
	return w
}
