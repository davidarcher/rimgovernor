package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ImmediateConcern is the closed safety scope; ordinary planning stays on its
// own inspection clock even when these concerns need repeated observation.
func ImmediateConcern(id ConcernID) bool {
	switch id {
	case ActiveCombat, CriticalMedicine, RestoreWorkers, MaintainFireSafety, MaintainShelter, RecoverDisasterServices:
		return true
	}
	return false
}

// ReviewFireSafety is shared by ordinary and immediate reviews. Other upkeep
// facts and latches cannot make a fresh fire observation fail or age.
func ReviewFireSafety(fires domain.Fact[[]UpkeepFire], previous, issued bool) (UpkeepNeed, error) {
	n := UpkeepNeed{Concern: MaintainFireSafety, Priority: 1, Active: previous || issued}
	if rows, known := fires.Value(); known {
		seen := map[string]bool{}
		selected := []string{}
		total, measured := 0.0, true
		for _, row := range rows {
			size, known := row.Size.Value()
			if !foodID(row.ID) || seen[row.ID] || known && (!foodNumber(size) || size < 0) {
				return n, errors.New("invalid upkeep fire")
			}
			seen[row.ID] = true
			if !row.Home {
				continue
			}
			selected = append(selected, row.ID)
			total += size
			measured = measured && known
			n.Unsafe = n.Unsafe || !known || size > 1
		}
		sort.Strings(selected)
		n.Targets = domain.Known(selected)
		n.Active = len(selected) > 0 || issued
		n.Unsafe = n.Unsafe || len(selected) > 3
		if measured {
			n.Metric = domain.Known(total)
		}
	} else if !n.Active {
		n.Priority = 4
	}
	return n, nil
}

func InspectImmediateRounds(f RoundsFacts, previous RoundsLatches, p RoundsPolicy) (RoundsFindings, error) {
	if err := p.Validate(); err != nil {
		return RoundsFindings{}, err
	}
	for _, count := range []domain.Fact[int64]{f.Hostiles, f.CriticalPatients, f.UrgentPatients} {
		if n, known := count.Value(); known && (n < 0 || n > math.MaxInt64/10) {
			return RoundsFindings{}, errors.New("invalid immediate count")
		}
	}
	fire, err := ReviewFireSafety(f.Upkeep.Fires, previous.Upkeep.Fire, f.UpkeepIssued[MaintainFireSafety])
	if err != nil {
		return RoundsFindings{}, err
	}
	c := &roundsRun{f: f, p: p, previous: previous, l: previous, r: RoundsFindings{Latches: previous, Disaster: f.Disaster}, upkeep: UpkeepReview{Needs: []UpkeepNeed{fire}}}
	c.r.Latches.Upkeep.Fire = fire.Active
	for _, d := range inspections {
		if !ImmediateConcern(d.Concern) {
			continue
		}
		goals, assessments := len(c.r.Concerns), len(c.r.Assessments)
		if d.Concern == RecoverDisasterServices {
			// Area protection has a fresh native input; service restoration does
			// not. Absence of an area change never clears an ongoing recovery.
			finding, priority := domain.FindingUnclear, 3
			if len(PlanSheltering(f)) > 0 {
				finding = domain.FindingUnmet
				trigger, _ := ShelterTriggerOf(f)
				priority = ShelterPriority(trigger)
			}
			c.r.Assessments = append(c.r.Assessments, RoundsAssessment{ID: d.Concern, Priority: priority, Finding: finding})
		} else if err := d.Inspect(c); err != nil {
			return RoundsFindings{}, err
		}
		if n, noOp := noOpOf(d.Concern, c.r.Concerns[goals:], c.r.Assessments[assessments:]); noOp {
			c.r.NoOps = append(c.r.NoOps, n)
		}
	}
	if methods, known := f.AvailableMethods.Value(); known {
		available := map[ConcernID]bool{}
		for _, id := range methods {
			if _, recognized := InspectionFor(id); !recognized || available[id] {
				return RoundsFindings{}, errors.New("invalid routine method capability " + string(id))
			}
			available[id] = true
		}
		for i, n := range c.r.Concerns {
			if n.Priority >= 3 && !available[n.ID] {
				c.r.Concerns[i].MethodUnavailable = true
			}
		}
		for i, n := range c.r.Assessments {
			if n.ID == MaintainFireSafety && n.Priority < 2 && !available[n.ID] {
				c.r.Assessments[i].MethodUnavailable = true
			}
		}
	}
	all := c.r.Assessments
	c.r.Assessments = nil
	for _, n := range all {
		if IsIncidentKind(n.ID) {
			c.r.Incidents = append(c.r.Incidents, n)
		} else {
			c.r.Assessments = append(c.r.Assessments, n)
		}
	}
	return c.r, nil
}
