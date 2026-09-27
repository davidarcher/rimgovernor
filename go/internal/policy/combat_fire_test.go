package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Six packed firing cells for three riflemen: each takes a cell a tile
// apart from the others.
func TestDecideCombatSpacesFiringCells(t *testing.T) {
	view := holdView()
	var firing []domain.Cell
	for x := int32(6); x <= 11; x++ {
		firing = append(firing, domain.Cell{X: x, Z: 23})
	}
	view.Layout = domain.Known(CombatLayout{Firing: firing, Toward: domain.North})
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	if len(memory.Roles) != 3 {
		t.Fatalf("%+v", memory.Roles)
	}
	for i, a := range memory.Roles {
		for _, b := range memory.Roles[i+1:] {
			if adjacent8(*a.Cell, *b.Cell) {
				t.Fatalf("%s at %v next to %s at %v", a.Pawn, *a.Cell, b.Pawn, *b.Cell)
			}
		}
	}
	// With no room to space, the rest pack rather than stand idle.
	if got := spaceCells([]domain.Cell{{X: 1}, {X: 2}, {X: 3}}); !reflect.DeepEqual(got, []domain.Cell{{X: 1}, {X: 3}, {X: 2}}) {
		t.Fatalf("%v", got)
	}
}

// An attack whose line crosses a colonist retargets to the top-scored
// hostile with a clear line, or is dropped when there is none; the
// formation's geometry ask names the shooters' cells, and a reaction stop
// with new attacks asks for their lines alone.
func TestDecideCombatNoAttackThroughColonist(t *testing.T) {
	view := threatView()
	_, ask, _ := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	if ask == nil || ask.Propose != RoleCoverBehindLine || len(ask.Cells) != 3 {
		t.Fatalf("formation ask %+v", ask)
	}
	a, b := domain.Cell{X: 9, Z: 23}, domain.Cell{X: 8, Z: 23}
	lines := []SightLine{
		{Cell: a, Hostile: "h5", LineOfFire: true, ColonistInPath: true},
		{Cell: a, Hostile: "h4", LineOfFire: true},
		{Cell: b, Hostile: "h5", LineOfFire: true, ColonistInPath: true},
		{Cell: b, Hostile: "h4", LineOfFire: true, ColonistInPath: true},
		{Cell: b, Hostile: "h3", LineOfFire: false},
	}
	orders, again, memory := DecideCombat(view, GeometryReply{Answered: true, Lines: lines}, StopEvent{}, CombatMemory{})
	want := []CombatOrder{
		{Pawn: "a", Kind: OrderAttack, Target: "h4", Reason: ReasonFormation},
		{Pawn: "c", Kind: OrderAttack, Target: "h5", Reason: ReasonFormation},
	}
	if again != nil || !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v", orders)
	}
	if memory.Roles[0].Target != "h4" {
		t.Fatalf("retarget not kept: %+v", memory.Roles)
	}
	// Next stop, b is still to order: its attack asks for lines only.
	view.Tick = 160
	for i := range view.Pawns[:3] {
		view.Pawns[i].Target = memory.Roles[i].Target
	}
	view.Pawns[1].Target = ""
	_, ask, _ = DecideCombat(view, GeometryReply{}, StopEvent{}, memory)
	if ask == nil || ask.Propose != "" || len(ask.Cells) != 3 || ask.Hostiles[0] != "h5" {
		t.Fatalf("line ask %+v", ask)
	}
	if orders, _, _ = DecideCombat(view, GeometryReply{Answered: true, Lines: lines}, StopEvent{}, memory); len(orders) != 0 {
		t.Fatalf("an attack through a colonist: %+v", orders)
	}
}

// A gunner whose target is in melee with our blocker gets stop and
// hold-fire; it holds while the melee lasts and gets fire-at-will back
// when it ends, then its attack.
func TestDecideCombatHoldsFireOnBlockerMelee(t *testing.T) {
	view := threatView()
	cell := func(x int32) *domain.Cell { return &domain.Cell{X: x, Z: 23} }
	memory := CombatMemory{Tactic: TacticHold, Formed: 50, Roles: []CombatRole{
		{Pawn: "a", Cell: cell(9), Target: "h1", Ranged: true},
		{Pawn: "b", Cell: cell(8), Target: "h1", Duty: DutyBlocker},
		{Pawn: "c", Cell: cell(10), Target: "h5", Ranged: true},
	}}
	// h1 and b fight; a is shooting h1, c is on h5.
	view.Pawns[0].Target, view.Pawns[0].Stance, view.Pawns[0].FireMode = "h1", StanceCooldown, FireAtWill
	view.Pawns[1].Target, view.Pawns[1].Stance = "h1", StanceMelee
	view.Pawns[2].Target, view.Pawns[2].FireMode = "h5", FireAtWill
	view.Pawns[3].Target, view.Pawns[3].Stance = "b", StanceMelee
	orders, memory := decideStop(t, view, StopEvent{Kind: "melee_contact"}, memory)
	want := []CombatOrder{
		{Pawn: "a", Kind: OrderStop, Reason: ReasonHoldFire},
		{Pawn: "a", Kind: OrderFireMode, FireMode: HoldFire, Reason: ReasonHoldFire},
	}
	if !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v", orders)
	}
	// Held while the melee lasts.
	view.Tick = 160
	view.Pawns[0].Target, view.Pawns[0].Stance, view.Pawns[0].FireMode = "", StanceIdle, HoldFire
	if orders, memory = decideStop(t, view, StopEvent{}, memory); len(orders) != 0 {
		t.Fatalf("%+v", orders)
	}
	// Melee over: fire at will again, and the attack at the next stop.
	view.Tick = 220
	view.Pawns[1].Stance, view.Pawns[3].Stance = StanceIdle, StanceIdle
	orders, memory = decideStop(t, view, StopEvent{}, memory)
	if want := []CombatOrder{{Pawn: "a", Kind: OrderFireMode, FireMode: FireAtWill, Reason: ReasonHoldFire}}; !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v", orders)
	}
	view.Tick = 280
	view.Pawns[0].FireMode = FireAtWill
	orders, _ = decideStop(t, view, StopEvent{}, memory)
	if want := []CombatOrder{{Pawn: "a", Kind: OrderAttack, Target: "h1", Reason: ReasonFormation}}; !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v", orders)
	}
}
