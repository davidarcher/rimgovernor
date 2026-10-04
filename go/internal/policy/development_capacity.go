package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DevelopmentHold is labor spoken for ahead of the ranked rows: a
// withheld prerequisite's work type, open optional work (Slot: it holds a
// development slot) or open startup/survival work (no slot, but its worker
// is busy).
type DevelopmentHold struct {
	Goal  GoalID       `json:",omitempty"`
	Labor LaborProfile `json:",omitempty"`
	Slot  bool         `json:",omitempty"`
}

// CommitmentHolds is the labor open commitments hold, the same at ranking
// and at admission: one hold per goal with an unresolved open action that
// is neither stalled nor released (a labor_idle row). Optional work holds
// a slot; startup/survival work holds only its worker. Withheld labor comes
// first, then goals in ID order.
func CommitmentHolds(commitments []Commitment, now domain.Tick, released map[GoalID]bool, withheld LaborProfile) []DevelopmentHold {
	var holds []DevelopmentHold
	for _, w := range withheld {
		holds = append(holds, DevelopmentHold{Labor: LaborProfile{w}})
	}
	byGoal := map[GoalID]DevelopmentHold{}
	for _, c := range commitments {
		v := c.Progress.View()
		if released[c.Goal] {
			continue
		}
		if !(v.Unresolved || v.Stage == domain.Pending || v.Stage == domain.Prepared || v.Stage == domain.Dispatched || v.Stage == domain.AwaitingObservation) {
			continue
		}
		slot := c.Source == PlayerGoal || c.Priority >= 3
		if prev, seen := byGoal[c.Goal]; seen && (prev.Slot || !slot) {
			continue
		}
		byGoal[c.Goal] = DevelopmentHold{Goal: c.Goal, Labor: append(LaborProfile(nil), c.Labor...), Slot: slot}
	}
	ids := make([]GoalID, 0, len(byGoal))
	for id := range byGoal {
		ids = append(ids, id)
	}
	sort.Slice(ids, func(i, j int) bool { return ids[i] < ids[j] })
	for _, id := range ids {
		holds = append(holds, byGoal[id])
	}
	return holds
}

// AdmitDevelopment revalidates, inside method admission, that the review
// still grants need a development slot: its row is still selected. A retry
// of the same admission is refused by the goal's revision before it gets
// here.
func AdmitDevelopment(s DevelopmentState, need GoalID) error {
	for _, row := range s.Rows {
		if row.Goal == need && row.Selected {
			return nil
		}
	}
	return fmt.Errorf("goal %s holds no development slot", need)
}
