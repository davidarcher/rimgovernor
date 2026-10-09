package policy

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// blockers are a memory's blocker cells by pawn.
func blockers(m CombatMemory) map[domain.PawnID]domain.Cell {
	out := map[domain.PawnID]domain.Cell{}
	for _, r := range m.Roles {
		if r.Duty == DutyBlocker && r.Cell != nil {
			out[r.Pawn] = *r.Cell
		}
	}
	return out
}

// {one approach, a layout choke, brawlers} -> blocker cells just
// outside the choke, a reserve held back.
func TestDecideCombatManhunterBlocksLayoutChoke(t *testing.T) {
	view := withAnimals(chokeView(), animal("w1", "Wolf_Timber", domain.Cell{X: 9, Z: 5}, 6.8), animal("w2", "Wolf_Timber", domain.Cell{X: 10, Z: 4}, 6.8))
	orders, ask, _ := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	if ask == nil || ask.Propose != RoleAdjacentToChoke || len(orders) != 0 {
		t.Fatalf("ask %+v", ask)
	}
	_, _, m := DecideCombat(view, GeometryReply{Answered: true, Proposals: chokeCells, Standable: ask.Cells}, StopEvent{}, CombatMemory{})
	if m.Tactic != TacticManhunter {
		t.Fatalf("%+v", m)
	}
	want := map[domain.PawnID]domain.Cell{"e": chokeCells[0], "f": chokeCells[1], "d": chokeCells[2]}
	if got := blockers(m); fmt.Sprint(got) != fmt.Sprint(want) {
		t.Fatalf("blockers %v, want %v", got, want)
	}
}

// waveView is a wave of n wolves split between the west door (9,15) of a
// room at x 10..19 and the east door (30,15) of a room at x 21..29, and
// brawlers d (armor .9) and e (.3); no layout.
func waveView(n int) CombatView {
	view := withBrawlers(holdView(), combatBrawler("d", .9), combatBrawler("e", .3))
	view.Layout = domain.Unknown[CombatLayout]()
	view.Rooms = []CombatRoom{
		{Interior: Rectangle{X: 10, Z: 10, Width: 10, Height: 10}, Doors: []domain.Cell{{X: 9, Z: 15}}},
		{Interior: Rectangle{X: 21, Z: 10, Width: 9, Height: 10}, Doors: []domain.Cell{{X: 30, Z: 15}}},
	}
	var pack []animalFacts
	for i := range n {
		cell := domain.Cell{X: int32(1 + i%3), Z: int32(14 + i)}
		if i%2 == 1 {
			cell = domain.Cell{X: int32(38 + i%3), Z: int32(14 + i)}
		}
		pack = append(pack, animal(PawnID(fmt.Sprintf("w%d", i)), "Wolf_Timber", cell, 6.8))
	}
	view = withAnimals(view, pack...)
	// Small animals: the pack does not outmatch us.
	for i := range view.Threats {
		view.Threats[i].BodySize = domain.Known(0.5)
	}
	return view
}

// {6 manhunters nearest two doors, 2 brawlers, rooms} -> one blocker on
// the inside cell of each door, the best armored on the busier door.
func TestDecideCombatManhunterWaveBlocksEachDoor(t *testing.T) {
	_, m := decideStop(t, waveView(7), StopEvent{}, CombatMemory{})
	want := map[domain.PawnID]domain.Cell{"d": {X: 10, Z: 15}, "e": {X: 29, Z: 15}}
	if got := blockers(m); fmt.Sprint(got) != fmt.Sprint(want) {
		t.Fatalf("blockers %v, want %v (%+v)", got, want, m.Roles)
	}
	for _, r := range m.Roles {
		if r.Duty == DutyBlocker && r.Target == "" {
			t.Fatalf("blocker without a target: %+v", r)
		}
	}
}

// {5 manhunters, two doors} is no wave: nobody blocks without a layout
// choke.
func TestDecideCombatManhunterSmallPackOneChoke(t *testing.T) {
	_, m := decideStop(t, waveView(5), StopEvent{}, CombatMemory{})
	if m.Tactic != TacticManhunter || len(blockers(m)) != 0 {
		t.Fatalf("%+v", m.Roles)
	}
}
