package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func doseView(hostiles int, hostile CombatPawnState) (CombatView, map[domain.PawnID]bool, map[domain.PawnID]CombatPawnState) {
	defender := CombatPawnState{ID: "d1", Cell: domain.Known(domain.Cell{X: 0, Z: 0}), WeaponRange: 25}
	view := CombatView{
		Pawns:     []CombatPawnState{defender},
		Defenders: []SquadDefenderFacts{squadDefender("d1", true)},
		Orderable: []domain.PawnID{"d1"},
	}
	for i := 0; i < hostiles; i++ {
		h := hostile
		h.ID = domain.PawnID("h" + string(rune('1'+i)))
		view.Pawns = append(view.Pawns, h)
		view.Threats = append(view.Threats, SquadThreatFacts{ID: PawnID(h.ID), BodySize: domain.Known(1.0)})
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	return view, map[domain.PawnID]bool{"d1": true}, state
}

func TestDoseOrders(t *testing.T) {
	near := CombatPawnState{Cell: domain.Known(domain.Cell{X: 30, Z: 0})}
	far := CombatPawnState{Cell: domain.Known(domain.Cell{X: 60, Z: 0})}
	cases := []struct {
		name     string
		hostiles int
		hostile  CombatPawnState
		want     bool
	}{
		{"lone rat is fought sober", 1, near, false},
		{"outmatched squad doses in reach", 2, near, true},
		{"outmatched squad waits out of reach", 2, far, false},
		{"one mech is dangerous", 1, CombatPawnState{Cell: near.Cell, Kind: "Mech_Lancer"}, true},
		{"one go-juiced raider is dangerous", 1, CombatPawnState{Cell: near.Cell, GoJuice: true}, true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			view, orderable, state := doseView(c.hostiles, c.hostile)
			if c.hostile.Kind == "Mech_Lancer" {
				view.Threats[0].Mech = true
			}
			var m CombatMemory
			got := doseOrders(view, &m, nil, orderable, state)
			if (len(got) == 1) != c.want {
				t.Fatalf("orders %v, want dose %v", got, c.want)
			}
			if c.want && (got[0] != CombatOrder{Pawn: "d1", Kind: OrderDrug, Drug: "GoJuice", Reason: ReasonDrug}) {
				t.Fatalf("order %+v", got[0])
			}
			if again := doseOrders(view, &m, nil, orderable, state); len(again) != 0 {
				t.Fatalf("dosed twice: %v", again)
			}
		})
	}
}

func TestDoseOrdersSkipsHighAndDowned(t *testing.T) {
	near := CombatPawnState{Cell: domain.Known(domain.Cell{X: 5, Z: 0})}
	for _, mod := range []func(*CombatPawnState){
		func(s *CombatPawnState) { s.GoJuice = true },
		func(s *CombatPawnState) { s.Downed = true },
	} {
		view, orderable, state := doseView(2, near)
		d := state["d1"]
		mod(&d)
		state["d1"] = d
		var m CombatMemory
		if got := doseOrders(view, &m, nil, orderable, state); len(got) != 0 {
			t.Fatalf("dosed %+v: %v", d, got)
		}
	}
}
