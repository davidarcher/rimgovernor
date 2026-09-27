package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// fallbackView is holdView formed and standing on its firing line, with
// the inner line one row further toward Home (z=24), everyone aiming.
func fallbackView(t *testing.T) (CombatView, CombatMemory) {
	t.Helper()
	view := holdView()
	layout, _ := view.Layout.Value()
	layout.Retreat = []domain.Cell{{X: 9, Z: 24}, {X: 8, Z: 24}, {X: 10, Z: 24}}
	view.Layout = domain.Known(layout)
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	if memory.Tactic != TacticHold {
		t.Fatalf("%+v", memory)
	}
	view.Tick = 200
	for i, cell := range layout.Firing {
		view.Pawns[i].Cell = domain.Known(cell)
		view.Pawns[i].Stance = StanceWarmup
	}
	return view, memory
}

func retreatOrder(pawn domain.PawnID, x, z int32) CombatOrder {
	return CombatOrder{Pawn: pawn, Kind: OrderMove, Cell: domain.Cell{X: x, Z: z}, Reason: ReasonRetreat}
}

// A healthy line gets no orders.
func TestDecideCombatHealthyLineHolds(t *testing.T) {
	view, memory := fallbackView(t)
	if orders, _ := decideStop(t, view, StopEvent{Kind: "entered_range"}, memory); len(orders) != 0 {
		t.Fatalf("%+v", orders)
	}
}

// A hostile at the cover row compromises the line: every defender goes to
// its own Retreat cell, through the aim guard, and the inner line then
// holds against the same hostile.
func TestDecideCombatFallsBackWhenLineCompromised(t *testing.T) {
	view, memory := fallbackView(t)
	view.Positional[0].Position = domain.Known(domain.Cell{X: 9, Z: 22})
	orders, next := decideStop(t, view, StopEvent{}, memory)
	want := []CombatOrder{retreatOrder("a", 9, 24), retreatOrder("b", 8, 24), retreatOrder("c", 10, 24)}
	if !reflect.DeepEqual(orders, want) || next.Tactic != TacticHold || next.Formed != memory.Formed {
		t.Fatalf("%+v %+v", orders, next)
	}
	for i, o := range want {
		view.Pawns[i].Cell = domain.Known(o.Cell)
	}
	view.Tick = 300
	if orders, again := decideStop(t, view, StopEvent{}, next); len(orders) != 0 || again.Tactic != TacticHold {
		t.Fatalf("the inner line did not hold: %+v %+v", orders, again)
	}
	// Past the inner line there is no further line: the hold re-forms.
	view.Positional[0].Position = domain.Known(domain.Cell{X: 9, Z: 23})
	if _, again := decideStop(t, view, StopEvent{}, next); again.Formed != 300 {
		t.Fatalf("a compromised inner line kept the hold: %+v", again)
	}
}

// A serious injury pulls the hurt defender back alone.
func TestDecideCombatPullsBackHurtDefender(t *testing.T) {
	view, memory := fallbackView(t)
	orders, next := decideStop(t, view, StopEvent{Kind: StopSeriousInjury, Pawn: "b"}, memory)
	if want := []CombatOrder{retreatOrder("b", 8, 24)}; !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v", orders)
	}
	// A later compromise pulls the rest back to their own cells.
	view.Pawns[1].Cell = domain.Known(domain.Cell{X: 8, Z: 24})
	view.Positional[1].Position = domain.Known(domain.Cell{X: 10, Z: 22})
	orders, _ = decideStop(t, view, StopEvent{}, next)
	if want := []CombatOrder{retreatOrder("a", 9, 24), retreatOrder("c", 10, 24)}; !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v", orders)
	}
}

// A breach pulls every defender back even with the raiders still in front.
func TestDecideCombatFallsBackOnBreach(t *testing.T) {
	view, memory := fallbackView(t)
	orders, next := decideStop(t, view, StopEvent{Kind: StopBreach}, memory)
	want := []CombatOrder{retreatOrder("a", 9, 24), retreatOrder("b", 8, 24), retreatOrder("c", 10, 24)}
	if !reflect.DeepEqual(orders, want) || next.Formed != memory.Formed {
		t.Fatalf("%+v %+v", orders, next)
	}
}

// A layout without an inner line (a record from before #860) keeps the
// old reaction: the breach re-forms.
func TestDecideCombatWithoutInnerLineReforms(t *testing.T) {
	view, memory := fallbackView(t)
	layout, _ := view.Layout.Value()
	layout.Retreat = nil
	view.Layout = domain.Known(layout)
	if _, next := decideStop(t, view, StopEvent{Kind: StopBreach}, memory); next.Formed != 200 {
		t.Fatalf("%+v", next)
	}
}

// Every Retreat cell is a distinct cell one step behind its firing cell,
// on the Home side of the line.
func TestDefenseLayoutRetreatIsBehindTheLine(t *testing.T) {
	layout, err := DefenseLayouts(defenseFixture())
	if err != nil || len(layout.Firing) == 0 {
		t.Fatal(layout.Firing, err)
	}
	v, _ := towardVector(layout.Toward)
	seen := map[domain.Cell]bool{}
	for _, f := range layout.Firing {
		seen[f.Cell] = true
	}
	for _, f := range layout.Firing {
		if seen[f.Retreat] || f.Retreat != (domain.Cell{X: f.Cell.X + v.X, Z: f.Cell.Z + v.Z}) {
			t.Fatalf("retreat %v for firing %v", f.Retreat, f.Cell)
		}
		seen[f.Retreat] = true
	}
}
