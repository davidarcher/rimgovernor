package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// sneakView is harassView with brawler m and asleep of the besiegers on
// LayDown.
func sneakView(asleep ...domain.PawnID) (CombatView, CombatMemory) {
	view, memory := harassView()
	view = withBrawlers(view, combatBrawler("m", 0.5))
	for i, p := range view.Pawns {
		for _, id := range asleep {
			if p.ID == id {
				view.Pawns[i].Job = "LayDown"
			}
		}
	}
	return view, memory
}

// {camp past the window, r1 and r2 asleep, 3 riflemen, 1 brawler} ->
// everyone attacks a besieger.
func TestDecideCombatSiegeSneaksOnSleepers(t *testing.T) {
	view, memory := sneakView("r1", "r2")
	orders, m := decideStop(t, view, StopEvent{}, memory)
	if m.SiegeMode != SiegeSneak {
		t.Fatalf("%+v", m)
	}
	a := attacks(orders)
	for _, id := range []domain.PawnID{"a", "b", "c", "m"} {
		if a[id] != "r1" && a[id] != "r2" {
			t.Errorf("%s attacks %q", id, a[id])
		}
	}
}

// {one of three besiegers asleep} -> no sneak: harass as before.
func TestDecideCombatSiegeAwakeCampNoSneak(t *testing.T) {
	view, memory := sneakView("r1")
	s3, d3 := combatRaider("r3", domain.Cell{X: 11, Z: -20})
	d3.LordJobClass, d3.LordToilClass = domain.Known(siegeLordJob), domain.Known(siegeCampToil)
	view.Threats, view.Positional = append(view.Threats, s3), append(view.Positional, d3)
	view.Pawns = append(view.Pawns, CombatPawnState{ID: "r3", Cell: domain.Known(domain.Cell{X: 11, Z: -20}), WeaponRange: 20})
	orders, m := decideStop(t, view, StopEvent{}, memory)
	if m.SiegeMode != SiegeHarass {
		t.Fatalf("%+v", m)
	}
	if a := attacks(orders); a["m"] != "" {
		t.Fatalf("brawler sorties: %v", a)
	}
}
