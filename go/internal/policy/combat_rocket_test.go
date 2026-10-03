package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// rocketView is holdView with rifleman a carrying a triple rocket launcher
// and a third raider r3 beside r1 and r2 (a clump of three at 9,5).
func rocketView() CombatView {
	view := holdView()
	s3, d3 := combatRaider("r3", domain.Cell{X: 8, Z: 5})
	view.Threats, view.Positional = append(view.Threats, s3), append(view.Positional, d3)
	view.Pawns = append(view.Pawns,
		CombatPawnState{ID: "r1", Cell: domain.Known(domain.Cell{X: 9, Z: 5})},
		CombatPawnState{ID: "r2", Cell: domain.Known(domain.Cell{X: 10, Z: 4})},
		CombatPawnState{ID: "r3", Cell: domain.Known(domain.Cell{X: 8, Z: 5})})
	view.Pawns[0].Weapon, view.Pawns[0].WeaponFacts, view.Pawns[0].WeaponRange = "Gun_TripleRocket", coreWeapons["Gun_TripleRocket"], 40
	return view
}

func groundOrders(orders []CombatOrder) []CombatOrder {
	var out []CombatOrder
	for _, o := range orders {
		if o.Kind == OrderAttackGround {
			out = append(out, o)
		}
	}
	return out
}

// TestRocketClump (#1051): {a rocket carrier, three raiders within 3 cells
// of each other in range} -> one ground shot at the nearest raider that
// counts all three;
// {two raiders} -> none; {a colonist within 5 cells of the clump} -> none;
// {the clump past the launcher's range} -> none.
func TestRocketClump(t *testing.T) {
	orders, _ := decideStop(t, rocketView(), StopEvent{}, CombatMemory{})
	want := CombatOrder{Pawn: "a", Kind: OrderAttackGround, Cell: domain.Cell{X: 8, Z: 5}, Reason: ReasonRocketClump}
	if got := groundOrders(orders); len(got) != 1 || got[0] != want {
		t.Fatalf("%+v", got)
	}
	cases := map[string]func(*CombatView){
		"two raiders": func(v *CombatView) { v.Threats, v.Pawns = v.Threats[:2], v.Pawns[:len(v.Pawns)-1] },
		"colonist near": func(v *CombatView) {
			v.Pawns[1].Cell = domain.Known(domain.Cell{X: 9, Z: 6})
		},
		"out of range": func(v *CombatView) { v.Pawns[0].WeaponRange = 20 },
		"no launcher":  func(v *CombatView) { v.Pawns[0] = withWeapon(v.Pawns[0], "Gun_AssaultRifle") },
	}
	for name, edit := range cases {
		view := rocketView()
		edit(&view)
		if orders, _ := decideStop(t, view, StopEvent{}, CombatMemory{}); len(groundOrders(orders)) != 0 {
			t.Errorf("%s: %+v", name, groundOrders(orders))
		}
	}
}
