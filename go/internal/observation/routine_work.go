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
		w := policy.WorkPawn{ID: p.ID}
		mental, mk := nativePresence(row.MentalState, row.Issues, "mental_state").Value()
		if dead || downed || row.GetDrafted() || mk && mental {
			w.Available = domain.Known(false)
		} else if row.Drafted != nil && mk {
			w.Available = domain.Known(true)
		}
		if s := row.Settings; s != nil {
			w.Applies = optional(s.WorkApplies)
			w.Manual = optional(s.ManualWorkPriorities)
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
		if b := row.Biography; b != nil && !hasIssue(b.Issues, "skills") {
			skills := []policy.WorkSkill{}
			known := true
			for _, entry := range b.Skills {
				if entry.Level == nil || entry.Disabled == nil || entry.Passion == nil {
					known = false
					break
				}
				skills = append(skills, policy.WorkSkill{Name: entry.Definition.GetDefName(), Level: int(entry.GetLevel()), Disabled: entry.GetDisabled(), Passion: entry.GetPassion()})
			}
			if known {
				w.Skills = domain.Known(skills)
			}
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
		rows = append(rows, w)
	}
	return domain.Known(rows)
}
