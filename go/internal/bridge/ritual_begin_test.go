package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A ritual begin builds a RitualIntent with the spot and the exact
// assignments.
func TestRitualBeginBuildsIntent(t *testing.T) {
	value, err := domain.NewRitualBegin("guide", "Precept_12", domain.Cell{X: 40, Z: 41},
		[]domain.RitualSlot{{Slot: "moralist", Pawns: []domain.PawnID{"guide"}}, {Slot: "candidate", Pawns: []domain.PawnID{"a", "b"}}}, []domain.PawnID{"s1"})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewRitualAction("ritual2", value)
	if err != nil {
		t.Fatal(err)
	}
	wire, err := ritualAction(action)
	if err != nil {
		t.Fatal(err)
	}
	got := wire.GetRitual()
	if got.GetPawnId() != "guide" || got.GetRitual() != "Precept_12" || got.GetVerb() != "begin" || got.GetSpot().GetX() != 40 || got.GetSpot().GetZ() != 41 {
		t.Fatalf("%v", got)
	}
	roles := got.GetRoles()
	if len(roles) != 2 || roles[0].GetSlot() != "candidate" || len(roles[0].GetPawnIds()) != 2 || roles[0].GetPawnIds()[1] != "b" || roles[1].GetSlot() != "moralist" {
		t.Fatalf("%v", roles)
	}
	if s := got.GetSpectatorPawnIds(); len(s) != 1 || s[0] != "s1" {
		t.Fatalf("%v", s)
	}
	start, _ := domain.NewRitual("pawn-7", domain.RitualBestowing, domain.RitualStart)
	startAction, _ := domain.NewRitualAction("ritual3", start)
	wire, err = ritualAction(startAction)
	if err != nil || wire.GetRitual().GetSpot() != nil || len(wire.GetRitual().GetRoles()) != 0 {
		t.Fatal("a start carries no spot or roles", wire, err)
	}
}
