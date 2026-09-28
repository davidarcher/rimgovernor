package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// sortieView is siegeView camped, inside the sortie window, with brawler m.
func sortieView() CombatView {
	return withBrawlers(siegeView(siegeCampToil), combatBrawler("m", 0.5))
}

// {camp in the window, r1 building a mortar frame, r2 a sandbag frame, 3
// riflemen, 1 brawler} -> riflemen attack the mortar frame; the brawler
// attacks a besieger.
func TestDecideCombatSiegeSnipesMortarFrames(t *testing.T) {
	view := sortieView()
	for i, p := range view.Pawns {
		switch p.ID {
		case "r1":
			view.Pawns[i].Job, view.Pawns[i].Target, view.Pawns[i].TargetMortar = "FinishFrame", "Thing_Frame9", true
		case "r2":
			view.Pawns[i].Job, view.Pawns[i].Target = "FinishFrame", "Thing_Frame3"
		}
	}
	orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	if m.SiegeMode != SiegeSortie {
		t.Fatalf("%+v", m)
	}
	a := attacks(orders)
	for _, id := range []domain.PawnID{"a", "b", "c"} {
		if a[id] != "Thing_Frame9" {
			t.Errorf("%s attacks %q", id, a[id])
		}
	}
	if a["m"] != "r1" && a["m"] != "r2" {
		t.Errorf("brawler attacks %q", a["m"])
	}
}

// {no builder} -> riflemen attack besiegers.
func TestDecideCombatSiegeWithoutBuildersShootsPawns(t *testing.T) {
	orders, _ := decideStop(t, sortieView(), StopEvent{}, CombatMemory{})
	a := attacks(orders)
	for _, id := range []domain.PawnID{"a", "b", "c"} {
		if a[id] != "r1" && a[id] != "r2" {
			t.Errorf("%s attacks %q", id, a[id])
		}
	}
}
