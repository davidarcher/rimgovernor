package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// combatBrawler is an eligible melee-only defender with a sharp armor
// rating.
func combatBrawler(id domain.PawnID, armor float64) SquadDefenderFacts {
	d := combatRifleman(id)
	d.RangedEquipped, d.MeleeEquipped, d.Armor = domain.Known(false), domain.Known(true), domain.Known(armor)
	return d
}

// chokeCells are the game's adjacent_to_choke proposals for the choke at
// (9,17): the three cells just past it on our (north) side.
var chokeCells = []domain.Cell{{X: 9, Z: 18}, {X: 8, Z: 18}, {X: 10, Z: 18}}

// chokeView is holdView with the corridor exit at (9,17) and brawlers d
// (armor .3), e (.9), f (.6) and g (.1) standing back at z=30.
func chokeView() CombatView {
	view := holdView()
	layout, _ := view.Layout.Value()
	layout.Choke = domain.Known(domain.Cell{X: 9, Z: 17})
	view.Layout = domain.Known(layout)
	for i, b := range []SquadDefenderFacts{combatBrawler("d", .3), combatBrawler("e", .9), combatBrawler("f", .6), combatBrawler("g", .1)} {
		view.Defenders = append(view.Defenders, b)
		view.Pawns = append(view.Pawns, CombatPawnState{ID: b.ID, Cell: domain.Known(domain.Cell{X: int32(4 + i), Z: 30}), Stance: StanceIdle})
		view.Orderable = append(view.Orderable, b.ID)
	}
	return view
}

// decideChoke runs one stop, answering an adjacent_to_choke ask with
// chokeCells.
func decideChoke(t *testing.T, view CombatView, stop StopEvent, memory CombatMemory) ([]CombatOrder, CombatMemory) {
	t.Helper()
	orders, ask, next := DecideCombat(view, GeometryReply{}, stop, memory)
	if ask == nil {
		return orders, next
	}
	layout, _ := view.Layout.Value()
	// Named: Formation's own cells, the cells around the line (#881), then
	// the shooters'.
	var cells []domain.Cell
	for _, c := range append(append(formationChecks(view.sorted(), layout), aroundLine(layout.Firing)...), shooterCells(view.sorted())...) {
		if !slices.Contains(cells, c) {
			cells = append(cells, c)
		}
	}
	want := &GeometryRequest{Propose: RoleAdjacentToChoke, Line: layout.Firing, Choke: domain.Cell{X: 9, Z: 17}, OurSide: domain.Cell{X: 9, Z: 20}, Hostiles: []domain.PawnID{"r1", "r2"}, Cells: cells}
	if !reflect.DeepEqual(ask, want) {
		t.Fatalf("ask %+v, want %+v", ask, want)
	}
	orders, _, next = DecideCombat(view, GeometryReply{Answered: true, Proposals: chokeCells, Standable: ask.Cells}, stop, memory)
	return orders, next
}

// {choke, brawlers by armor, gunners} → the three best-armored brawlers
// move to the cells just outside the choke, the next waits as reserve on
// its own spot, and the riflemen take the firing line down the choke.
func TestDecideCombatPlacesBlockersOutsideChoke(t *testing.T) {
	orders, memory := decideChoke(t, chokeView(), StopEvent{}, CombatMemory{})
	move := func(p domain.PawnID, x, z int32) CombatOrder {
		return CombatOrder{Pawn: p, Kind: OrderMove, Cell: domain.Cell{X: x, Z: z}, Reason: ReasonFormation}
	}
	want := []CombatOrder{move("a", 9, 23), move("b", 8, 23), move("c", 10, 23), move("d", 10, 18), move("e", 9, 18), move("f", 8, 18)}
	if !reflect.DeepEqual(orders, want) || memory.Tactic != TacticHold {
		t.Fatalf("%+v\nwant %+v\n%+v", orders, want, memory)
	}
	duties := map[domain.PawnID]CombatDuty{}
	for _, r := range memory.Roles {
		duties[r.Pawn] = r.Duty
	}
	if duties["e"] != DutyBlocker || duties["f"] != DutyBlocker || duties["d"] != DutyBlocker || duties["g"] != DutyReserve || duties["a"] != "" {
		t.Fatalf("%+v", duties)
	}
}

// Two brawlers: one blocks, one is held in reserve.
func TestDecideCombatKeepsAReserveBrawler(t *testing.T) {
	view := chokeView()
	view.Defenders = view.Defenders[:5] // a, b, c, d, e
	_, memory := decideChoke(t, view, StopEvent{}, CombatMemory{})
	var blockers, reserves []domain.PawnID
	for _, r := range memory.Roles {
		switch r.Duty {
		case DutyBlocker:
			blockers = append(blockers, r.Pawn)
		case DutyReserve:
			reserves = append(reserves, r.Pawn)
		}
	}
	if !reflect.DeepEqual(blockers, []domain.PawnID{"e"}) || !reflect.DeepEqual(reserves, []domain.PawnID{"d"}) {
		t.Fatalf("blockers %v reserves %v", blockers, reserves)
	}
}

// {serious injury on a blocker} → the reserve moves into the blocker's
// cell and the hurt blocker pulls back to the reserve's place (a retreat,
// through its melee cooldown).
func TestDecideCombatSwapsHurtBlocker(t *testing.T) {
	view := chokeView()
	_, memory := decideChoke(t, view, StopEvent{}, CombatMemory{})
	view.Tick = 400
	at := map[domain.PawnID]domain.Cell{"a": {X: 9, Z: 23}, "b": {X: 8, Z: 23}, "c": {X: 10, Z: 23}, "e": {X: 9, Z: 18}, "f": {X: 8, Z: 18}, "d": {X: 10, Z: 18}, "g": {X: 7, Z: 30}}
	for i := range view.Pawns {
		view.Pawns[i].Cell, view.Pawns[i].Stance = domain.Known(at[view.Pawns[i].ID]), StanceIdle
		if i < 3 {
			view.Pawns[i].Target = "r1"
		}
	}
	view.Pawns[4].Stance = StanceCooldown // e, in melee cooldown
	orders, next := decideChoke(t, view, StopEvent{Kind: StopSeriousInjury, Pawn: "e", Target: "r1"}, memory)
	want := []CombatOrder{
		{Pawn: "e", Kind: OrderMove, Cell: domain.Cell{X: 7, Z: 30}, Reason: ReasonRetreat},
		{Pawn: "g", Kind: OrderMove, Cell: domain.Cell{X: 9, Z: 18}, Reason: ReasonFormation},
	}
	if !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v", orders)
	}
	// A second injury, on a blocker with no reserve left, pulls no one.
	view.Tick = 460
	view.Pawns[4].Stance, view.Pawns[6].Stance = StanceMoving, StanceMoving
	if orders, _ := decideChoke(t, view, StopEvent{Kind: StopSeriousInjury, Pawn: "f"}, next); len(orders) != 0 {
		t.Fatalf("no reserve left, yet %+v", orders)
	}
}

// Without a brawler the hold asks for cover behind the line as before.
func TestDecideCombatNoBrawlerNoBlocking(t *testing.T) {
	view := holdView()
	layout, _ := view.Layout.Value()
	layout.Choke = domain.Known(domain.Cell{X: 9, Z: 17})
	view.Layout = domain.Known(layout)
	if _, ask, _ := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{}); ask == nil || ask.Propose != RoleCoverBehindLine {
		t.Fatalf("%+v", ask)
	}
}
