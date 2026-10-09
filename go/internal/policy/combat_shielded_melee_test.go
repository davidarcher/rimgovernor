package policy

import (
	"fmt"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// shieldAll gives every hostile pawn in view a mace and a full shield.
func shieldAll(view CombatView) CombatView {
	hostile := map[domain.PawnID]bool{}
	for _, t := range view.Threats {
		hostile[domain.PawnID(t.ID)] = true
	}
	for i := range view.Pawns {
		if hostile[view.Pawns[i].ID] {
			view.Pawns[i].Weapon, view.Pawns[i].WeaponRange, view.Pawns[i].Shield = "MeleeWeapon_Mace", 1, domain.Known(1.0)
		}
	}
	return view
}

// withRaiders replaces a view's hostiles with raiders at cells.
func withRaiders(view CombatView, cells ...domain.Cell) CombatView {
	view.Threats, view.Positional = nil, nil
	for i, c := range cells {
		id := PawnID(fmt.Sprintf("s%d", i))
		s, d := combatRaider(id, c)
		view.Threats, view.Positional = append(view.Threats, s), append(view.Positional, d)
		view.Pawns = append(view.Pawns, CombatPawnState{ID: domain.PawnID(id), Cell: domain.Known(c), Stance: StanceMoving})
	}
	return view
}

// {lab-choke: a shielded melee charge, one choke, brawlers} -> the hold's
// Melee blockers just outside the choke.
func TestShieldedMeleeBlocks(t *testing.T) {
	view := shieldAll(withRaiders(chokeView(), domain.Cell{X: 9, Z: 5}, domain.Cell{X: 10, Z: 4}))
	_, ask, _ := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	if ask == nil || ask.Propose != RoleAdjacentToChoke {
		t.Fatalf("ask %+v", ask)
	}
	_, _, m := DecideCombat(view, GeometryReply{Answered: true, Proposals: chokeCells, Standable: ask.Cells}, StopEvent{}, CombatMemory{})
	want := map[domain.PawnID]domain.Cell{"e": chokeCells[0], "f": chokeCells[1], "d": chokeCells[2]}
	if got := blockers(m); m.Tactic != TacticHold || fmt.Sprint(got) != fmt.Sprint(want) {
		t.Fatalf("%s blockers %v, want %v", m.Tactic, got, want)
	}
}

// splitView is lab-choke with two room doors and a charge of four
// raiders, two nearest each door.
func splitView() CombatView {
	view := chokeView()
	view.Rooms = []CombatRoom{
		{Interior: Rectangle{X: 10, Z: 10, Width: 10, Height: 10}, Doors: []domain.Cell{{X: 9, Z: 15}}},
		{Interior: Rectangle{X: 21, Z: 10, Width: 9, Height: 10}, Doors: []domain.Cell{{X: 30, Z: 15}}},
	}
	return withRaiders(view, domain.Cell{X: 1, Z: 14}, domain.Cell{X: 2, Z: 16}, domain.Cell{X: 38, Z: 14}, domain.Cell{X: 39, Z: 16})
}

// {a shielded melee charge approaching two doors} -> blockers inside each
// door (the multi-choke rule); unshielded raiders keep one choke.
func TestShieldedMeleeSplitsChokes(t *testing.T) {
	west, east := domain.Cell{X: 10, Z: 15}, domain.Cell{X: 29, Z: 15}
	_, m := decideStop(t, shieldAll(splitView()), StopEvent{}, CombatMemory{})
	doors := map[domain.Cell]bool{}
	for _, c := range blockers(m) {
		doors[c] = true
	}
	if m.Tactic != TacticHold || !doors[west] || !doors[east] {
		t.Fatalf("%s blockers %v (%+v)", m.Tactic, blockers(m), m.Roles)
	}
	_, m = decideStop(t, splitView(), StopEvent{}, CombatMemory{})
	for _, c := range blockers(m) {
		if c == west || c == east {
			t.Fatalf("unshielded raiders split the chokes: %v", blockers(m))
		}
	}
}

// {an EMP carrier on the line, the three-raider clump shielded melee} ->
// attack_ground at the clump, dropping all three shields.
func TestShieldedMeleeEMP(t *testing.T) {
	view := grenadeView("Weapon_GrenadeEMP")
	for i := range view.Pawns {
		switch view.Pawns[i].ID {
		case "h1", "h2", "h3":
			view.Pawns[i].Weapon, view.Pawns[i].Shield = "MeleeWeapon_Mace", domain.Known(1.0)
		}
	}
	orders, _ := decideStop(t, view, StopEvent{}, CombatMemory{})
	want := []CombatOrder{{Pawn: "a", Kind: OrderAttackGround, Cell: domain.Cell{X: 9, Z: 13}, Reason: ReasonRocketClump}}
	if got := groundOrders(orders); !reflect.DeepEqual(got, want) {
		t.Fatalf("%+v", orders)
	}
}
