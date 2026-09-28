package policy

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// {humanoid raid weaker than us, outdoor temperature} -> extreme cold
// (-30 C) or heat (50 C) waits indoors: no attacks, the room doors closed
// and forbidden; mild (-5 C) or unknown weather fights.
func TestShelterInExtremeCold(t *testing.T) {
	for _, tc := range []struct {
		temp domain.Fact[float64]
		wait bool
	}{
		{domain.Known(-30.0), true},
		{domain.Known(50.0), true},
		{domain.Known(-5.0), false},
		{domain.Fact[float64]{}, false},
	} {
		view := raidView(2)
		view.OutdoorTemperatureC = tc.temp
		orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
		if m.Wait != tc.wait {
			t.Fatalf("%+v: wait %v", tc.temp, m.Wait)
		}
		if !tc.wait {
			continue
		}
		doors := 0
		for _, o := range orders {
			switch o.Kind {
			case OrderAttack:
				t.Fatalf("attack while sheltering: %+v", orders)
			case OrderDoor:
				doors++
			}
		}
		if doors != 4 {
			t.Fatalf("%+v", orders)
		}
	}
}

// {hold, line manned, then outmatched with two of three line cells lost}
// -> every defender runs 20 cells from the raid (retreat moves, no
// attacks) and the damaged door is queued; once no longer outmatched the
// fight drops its roles, re-forms, and the door goes to the nearest
// defender to repair; a repaired door leaves the queue.
func TestScatterLastResort(t *testing.T) {
	view, memory := fallbackView(t)
	view.DamagedDoors = []domain.Cell{{X: 9, Z: 30}}
	for _, th := range view.Positional {
		c, _ := th.Position.Value()
		view.Pawns = append(view.Pawns, CombatPawnState{ID: domain.PawnID(th.ID), Kind: "Pirate", Cell: domain.Known(c)})
	}
	_, memory = decideStop(t, view, StopEvent{}, memory)
	if memory.LineHeld != 3 || memory.Scattered {
		t.Fatalf("%+v", memory)
	}
	calm := view
	calm.Pawns = append([]CombatPawnState(nil), view.Pawns...)
	for i := range 3 {
		id, cell := PawnID(fmt.Sprintf("x%d", i)), domain.Cell{X: int32(8 + i), Z: 6}
		s, d := combatRaider(id, cell)
		view.Threats, view.Positional = append(view.Threats, s), append(view.Positional, d)
		view.Pawns = append(view.Pawns, CombatPawnState{ID: domain.PawnID(id), Kind: "Pirate", Cell: domain.Known(cell)})
	}
	// Outmatched but the line is still manned: no scatter.
	if _, m := decideStop(t, view, StopEvent{}, memory); m.Scattered {
		t.Fatalf("scattered on a manned line: %+v", m)
	}
	view.Tick = 300
	view.Pawns[1].Cell = domain.Known(domain.Cell{X: 8, Z: 27})
	view.Pawns[2].Cell = domain.Known(domain.Cell{X: 11, Z: 28})
	orders, m := decideStop(t, view, StopEvent{}, memory)
	if !m.Scattered || len(m.Repairs) != 1 || m.Repairs[0] != (domain.Cell{X: 9, Z: 30}) {
		t.Fatalf("%+v", m)
	}
	moved := map[domain.PawnID]bool{}
	for _, o := range orders {
		if o.Kind != OrderMove || o.Reason != ReasonRetreat || o.Cell.Z < 40 {
			t.Fatalf("not scattering: %+v", orders)
		}
		moved[o.Pawn] = true
	}
	if len(moved) != 3 {
		t.Fatalf("%+v", orders)
	}
	// The raid thins out: no longer outmatched, the fight re-forms.
	calm.Tick, calm.Pawns[1].Cell, calm.Pawns[2].Cell = 400, view.Pawns[1].Cell, view.Pawns[2].Cell
	_, m = decideStop(t, calm, StopEvent{}, m)
	if m.Scattered || len(m.Roles) != 0 || len(m.Repairs) != 1 {
		t.Fatalf("%+v", m)
	}
	calm.Tick = 500
	for i := range calm.Pawns {
		// Mid-aim a pawn would finish its shot first.
		calm.Pawns[i].Stance = StanceIdle
	}
	orders, m = decideStop(t, calm, StopEvent{}, m)
	repairs := 0
	for _, o := range orders {
		if o.Kind == OrderRepair {
			if o.Cell != (domain.Cell{X: 9, Z: 30}) || o.Pawn != "c" {
				t.Fatalf("%+v", o)
			}
			repairs++
		}
	}
	if repairs != 1 {
		t.Fatalf("%+v %+v", orders, m)
	}
	calm.Tick, calm.DamagedDoors = 600, nil
	if _, m = decideStop(t, calm, StopEvent{}, m); len(m.Repairs) != 0 || role(m, "c").Repair != nil {
		t.Fatalf("%+v", m)
	}
}
