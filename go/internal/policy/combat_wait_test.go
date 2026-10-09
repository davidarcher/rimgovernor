package policy

import (
	"fmt"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// raidView is waitView with the pack replaced by n pirates at z=4 and no
// hold-the-line layout (the squad fallback).
func raidView(n int) CombatView {
	view := waitView(0)
	view.Layout = domain.Fact[CombatLayout]{}
	view.Pawns = slices.DeleteFunc(view.Pawns, func(p CombatPawnState) bool { return p.ID != "a" && p.ID != "b" && p.ID != "c" })
	for i := range n {
		id, cell := PawnID(fmt.Sprintf("r%d", i)), domain.Cell{X: int32(i), Z: 4}
		s, d := combatRaider(id, cell)
		view.Threats, view.Positional = append(view.Threats, s), append(view.Positional, d)
		view.Pawns = append(view.Pawns, CombatPawnState{ID: domain.PawnID(id), Kind: "Pirate", Cell: domain.Known(cell)})
	}
	return view
}

// {humanoid raid stronger than us, no hold layout} -> no attacks, the
// squad holds where it stands behind closed, forbidden doors and stays
// there; past the give-up window (raiders still here) the wait ends: the
// doors are allowed and the fight re-forms. {raid weaker than us} -> no
// wait. {a complete hold layout} -> the killbox fights, no wait.
func TestWaitOutRaid(t *testing.T) {
	if _, m := decideStop(t, raidView(2), StopEvent{}, CombatMemory{}); m.Wait {
		t.Fatalf("waiting on a weaker raid: %+v", m)
	}
	held := raidView(5)
	held.Layout = waitView(0).Layout
	if _, m := decideStop(t, held, StopEvent{}, CombatMemory{}); m.Wait || m.Tactic != TacticHold {
		t.Fatalf("killbox waiting: %+v", m)
	}
	view := raidView(5)
	view.Tick = 1000
	orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	if !m.Wait || m.WaitSince != 1000 {
		t.Fatalf("%+v", m)
	}
	doors := 0
	for _, o := range orders {
		switch o.Kind {
		case OrderAttack:
			t.Fatalf("attack while waiting: %+v", orders)
		case OrderMove:
			t.Fatalf("move while waiting without an inner line: %+v", o)
		case OrderDoor:
			doors++
		}
	}
	if doors != 4 {
		t.Fatalf("%+v", orders)
	}
	view.Tick = 1000 + raidGiveUpTicks - 1
	orders, m = decideStop(t, view, StopEvent{}, m)
	if !m.Wait {
		t.Fatalf("wait ended inside the give-up window: %+v", m)
	}
	for _, o := range orders {
		if o.Kind == OrderAttack || o.Kind == OrderDoor {
			t.Fatalf("%+v", orders)
		}
	}
	view.Tick = 1000 + raidGiveUpTicks
	orders, m = decideStop(t, view, StopEvent{}, m)
	allows := 0
	for _, o := range orders {
		if o.Kind == OrderDoor && o.Door == DoorAllow {
			allows++
		}
	}
	if m.Wait || allows != 2 {
		t.Fatalf("%+v %+v", m, orders)
	}
}

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
	if !m.Wait {
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
	if m.Wait {
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

// {a raid waiting behind room doors (15,19) wooden and (20,25) broken,
// 30 plasteel} -> a plasteel door over (15,19) and a wall on the floor
// cell behind (20,25), (19,25); {10 plasteel} -> the wall only; {not
// waiting} -> nothing.
func TestWallBehindBrokenDoor(t *testing.T) {
	view := raidView(5)
	view.Tick = 1000
	_, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	want := []WaitDoor{{Door: domain.Cell{X: 15, Z: 19}, Inside: domain.Cell{X: 15, Z: 20}}, {Door: domain.Cell{X: 20, Z: 25}, Inside: domain.Cell{X: 19, Z: 25}}}
	if !m.Wait || !slices.Equal(m.WaitRooms, want) {
		t.Fatalf("%+v", m.WaitRooms)
	}
	census := map[domain.Cell]WaitDoorCell{
		{X: 15, Z: 19}: {Edifice: "Door", Stuff: "WoodLog"},
		{X: 15, Z: 20}: {Walkable: true},
		{X: 20, Z: 25}: {Walkable: true},
		{X: 19, Z: 25}: {Walkable: true},
	}
	builds := func(plasteel int64, m CombatMemory) []string {
		b, err := WaitHardening(m, census, plasteel, "Door", "Wall", "WoodLog")
		if err != nil {
			t.Fatal(err)
		}
		var out []string
		for _, x := range b {
			out = append(out, fmt.Sprintf("%s/%s@%d,%d", x.Definition(), x.Stuff(), x.Cell().X, x.Cell().Z))
		}
		return out
	}
	if got := builds(30, m); !slices.Equal(got, []string{"Door/Plasteel@15,19", "Wall/WoodLog@19,25"}) {
		t.Fatal(got)
	}
	if got := builds(10, m); !slices.Equal(got, []string{"Wall/WoodLog@19,25"}) {
		t.Fatal(got)
	}
	census[domain.Cell{X: 15, Z: 19}] = WaitDoorCell{Edifice: "Door", Stuff: "Plasteel"}
	if got := builds(30, m); !slices.Equal(got, []string{"Wall/WoodLog@19,25"}) {
		t.Fatal(got)
	}
	m.Wait = false
	if got := builds(30, m); got != nil {
		t.Fatal(got)
	}
}
