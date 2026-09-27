package policy

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// podRaiders puts n pod raiders out in the landing room, their lord on
// toil.
func podRaiders(view *CombatView, n int, toil string) {
	view.Threats, view.Positional = nil, nil
	for i := 0; i < n; i++ {
		s, d := combatRaider(PawnID(fmt.Sprintf("r%d", i)), domain.Cell{X: int32(8 + i), Z: 14})
		d.LordToilClass = domain.Known(toil)
		view.Threats, view.Positional = append(view.Threats, s), append(view.Positional, d)
	}
}

func TestPodStrength(t *testing.T) {
	hurt := func(v *CombatView) {
		for i := range v.Defenders {
			v.Defenders[i].HealthFraction = domain.Known(0.6)
		}
	}
	for _, tc := range []struct {
		name         string
		edit         func(*CombatView)
		ours, theirs float64
	}{
		{"closed pods count by landing cell", func(*CombatView) {}, 3, 1},
		{"live raiders once out", func(v *CombatView) { podRaiders(v, 2, "LordToil_AssaultColony") }, 3, 2},
		{"downed raiders drop out", func(v *CombatView) {
			podRaiders(v, 2, "LordToil_AssaultColony")
			v.Positional[0].Downed = domain.Known(true)
		}, 3, 1},
		{"hurt defenders weigh their health", func(v *CombatView) { hurt(v); podRaiders(v, 2, "LordToil_AssaultColony") }, 1.8, 2},
		{"a non-combatant adds nothing", func(v *CombatView) { v.Defenders = v.Defenders[3:] }, 0, 1},
	} {
		t.Run(tc.name, func(t *testing.T) {
			view := podsView()
			tc.edit(&view)
			pods, _ := view.Pods.Value()
			ours, theirs := podStrength(view, pods)
			if fmt.Sprintf("%.2f %.2f", ours, theirs) != fmt.Sprintf("%.2f %.2f", tc.ours, tc.theirs) {
				t.Fatalf("got %v vs %v, want %v vs %v", ours, theirs, tc.ours, tc.theirs)
			}
		})
	}
}

// Outmatched (three riflemen against four raiders), the doorway pairs
// hold behind the landing room's door, which is closed, and no one attacks.
func TestDecideCombatPodsWaitWhenOutmatched(t *testing.T) {
	view := doorwayView()
	podRaiders(&view, 4, "LordToil_AssaultColony")
	orders, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	if !memory.PodWait || memory.Tactic != TacticPods {
		t.Fatalf("%+v", memory)
	}
	door := false
	for _, o := range orders {
		switch {
		case o.Kind == OrderAttack:
			t.Fatalf("an attack while waiting: %+v", orders)
		case o.Kind == OrderDoor:
			door = o.Cell == domain.Cell{X: 10, Z: 11} && o.Door == DoorClose
		}
	}
	if !door {
		t.Fatalf("door not closed: %+v", orders)
	}
	// An even fight does not wait.
	podRaiders(&view, 3, "LordToil_AssaultColony")
	if _, even := decideStop(t, view, StopEvent{}, CombatMemory{}); even.PodWait {
		t.Fatalf("%+v", even)
	}
}

// Waiting, a raid phase change to loot-and-leave strikes: the door is held
// open and the doorway pairs attack. A phase change that is not flee or
// loot keeps waiting.
func TestDecideCombatPodsStrikeOnFlee(t *testing.T) {
	view := doorwayView()
	podRaiders(&view, 4, "LordToil_AssaultColony")
	_, waiting := decideStop(t, view, StopEvent{}, CombatMemory{})
	view.Tick = 400
	view.Pawns[0].Cell = domain.Known(domain.Cell{X: 11, Z: 10})
	view.Pawns[2].Cell = domain.Known(domain.Cell{X: 9, Z: 10})
	view.Pawns[3].Cell = domain.Known(domain.Cell{X: 33, Z: 0})
	if orders, still := decideStop(t, view, StopEvent{Kind: StopRaidPhase}, waiting); !still.PodWait || len(orders) != 0 {
		t.Fatalf("%+v %+v", orders, still)
	}
	podRaiders(&view, 4, "LordToil_StealCover")
	orders, struck := decideStop(t, view, StopEvent{Kind: StopRaidPhase}, waiting)
	if struck.PodWait || !struck.PodStruck {
		t.Fatalf("%+v", struck)
	}
	open, attacks := false, map[domain.PawnID]bool{}
	for _, o := range orders {
		if o.Kind == OrderDoor && o.Door == DoorHoldOpen && o.Cell == (domain.Cell{X: 10, Z: 11}) {
			open = true
		}
		if o.Kind == OrderAttack {
			attacks[o.Pawn] = true
		}
	}
	if !open || !attacks["a"] || !attacks["c"] {
		t.Fatalf("%+v", orders)
	}
}
