package policy

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// waitView is kiteView's line and inner line with riflemen a, b, c facing
// n wolves of body size 1, and a planned room with doors (15,19), (20,25).
func waitView(n int) CombatView {
	view := kiteView(6.8)
	view.Rooms = []CombatRoom{{Interior: Rectangle{X: 10, Z: 20, Width: 10, Height: 10}, Doors: []domain.Cell{{X: 15, Z: 19}, {X: 20, Z: 25}}}}
	var pack []animalFacts
	for i := range n {
		pack = append(pack, animal(PawnID(fmt.Sprintf("w%d", i)), "Wolf_Timber", domain.Cell{X: int32(i), Z: 4}, 6.8))
	}
	return withAnimals(view, pack...)
}

// {pack stronger than us} -> no attack orders, retreat moves to the inner
// line.
func TestDecideCombatManhunterWaitsWhenOutmatched(t *testing.T) {
	orders, m := decideStop(t, waitView(5), StopEvent{}, CombatMemory{})
	if !m.ManhunterWait {
		t.Fatalf("%+v", m)
	}
	moves := 0
	for _, o := range orders {
		switch o.Kind {
		case OrderAttack:
			t.Fatalf("attack while waiting: %+v", orders)
		case OrderMove:
			if o.Reason != ReasonRetreat || o.Cell.Z < 24 {
				t.Fatalf("%+v", o)
			}
			moves++
		}
	}
	if moves != 3 {
		t.Fatalf("%+v", orders)
	}
}

// Waiting closes and forbids every planned room door, once.
func TestDecideCombatManhunterWaitForbidsDoors(t *testing.T) {
	orders, m := decideStop(t, waitView(5), StopEvent{}, CombatMemory{})
	got := map[string]bool{}
	for _, o := range orders {
		if o.Kind == OrderDoor {
			got[fmt.Sprint(o.Cell, o.Door)] = true
		}
	}
	for _, d := range []domain.Cell{{X: 15, Z: 19}, {X: 20, Z: 25}} {
		for _, mode := range []DoorMode{DoorClose, DoorForbid} {
			if !got[fmt.Sprint(d, mode)] {
				t.Fatalf("missing %v %v: %+v", d, mode, orders)
			}
		}
	}
	view := waitView(5)
	view.Tick = 160
	orders, _ = decideStop(t, view, StopEvent{}, m)
	for _, o := range orders {
		if o.Kind == OrderDoor {
			t.Fatalf("door order repeated: %+v", orders)
		}
	}
}

// {strength flips} -> the fight re-forms and engages, and the forbidden
// doors are allowed again.
func TestDecideCombatManhunterWaitEndsWhenStronger(t *testing.T) {
	_, m := decideStop(t, waitView(5), StopEvent{}, CombatMemory{})
	view := waitView(2)
	view.Tick = 160
	orders, m := decideStop(t, view, StopEvent{}, m)
	if m.ManhunterWait {
		t.Fatalf("%+v", m)
	}
	allows, attacks := 0, 0
	for _, o := range orders {
		if o.Kind == OrderDoor && o.Door == DoorAllow {
			allows++
		}
		if o.Kind == OrderAttack || o.Kind == OrderMove && o.Reason == ReasonFormation {
			attacks++
		}
	}
	if allows != 2 || attacks == 0 {
		t.Fatalf("%+v", orders)
	}
}
