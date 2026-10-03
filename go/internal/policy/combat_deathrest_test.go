package policy

import (
	"testing"
)

// A deathresting defender (#1690) is in no planner's roster: no stop's
// orders, formation or flank, name it.
func TestDeathrestingDefenderTakesNoCombatOrder(t *testing.T) {
	view := flankView(6)
	for i := range view.Defenders {
		if view.Defenders[i].ID == "e" {
			view.Defenders[i].Deathresting = true
		}
	}
	memory := CombatMemory{}
	for stop := 0; stop < 3; stop++ {
		orders, _, next := DecideCombat(view, GeometryReply{Answered: true, Role: RoleFiringCells, Proposals: flankProposals(), Standable: flankProposals()}, StopEvent{}, memory)
		for _, o := range orders {
			if o.Pawn == "e" {
				t.Fatalf("stop %d ordered the deathresting pawn: %+v", stop, o)
			}
		}
		for _, r := range next.Roles {
			if r.Pawn == "e" {
				t.Fatalf("stop %d gave the deathresting pawn a role: %+v", stop, r)
			}
		}
		memory = next
		view.Tick += 60
	}
}
