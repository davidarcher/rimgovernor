package policy

import (
	"math/rand"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// combatRifleman is an eligible, undrafted rifleman.
func combatRifleman(id domain.PawnID) SquadDefenderFacts {
	f := domain.Known(false)
	return SquadDefenderFacts{ID: id, Dead: f, Downed: f, Drafted: f, MentalState: f, PlayerForced: f, DraftOwned: f, QueuedJobs: domain.Known(uint32(0)),
		ViolenceCapable: domain.Known(true), NeedsTend: f, HealthFraction: domain.Known(1.0), RangedEquipped: domain.Known(true), MeleeEquipped: f, Armed: domain.Known(true)}
}

// combatRaider is a walk-in raider at cell, in front of a north-facing line.
func combatRaider(id PawnID, cell domain.Cell) (SquadThreatFacts, DefensiveThreatFacts) {
	f := domain.Known(false)
	return SquadThreatFacts{ID: id, Dead: f, Downed: f, Humanlike: domain.Known(true), Animal: f, Manhunter: f, Hunting: f, RangedEquipped: f},
		DefensiveThreatFacts{ID: id, Dead: f, Downed: f, Humanlike: domain.Known(true), LordJobClass: domain.Known("LordJob_AssaultColony"), LordToilClass: domain.Known("LordToil_AssaultColony"),
			NearestColonistDistance: domain.Known(40.0), Position: domain.Known(cell)}
}

// holdView is a complete firing line at z=23 facing north, riflemen a, b
// and c, and raiders r1 and r2 walking in from the south.
func holdView() CombatView {
	s1, d1 := combatRaider("r1", domain.Cell{X: 9, Z: 5})
	s2, d2 := combatRaider("r2", domain.Cell{X: 10, Z: 4})
	return CombatView{
		Tick:       100,
		Defenders:  []SquadDefenderFacts{combatRifleman("a"), combatRifleman("b"), combatRifleman("c")},
		Threats:    []SquadThreatFacts{s1, s2},
		Positional: []DefensiveThreatFacts{d1, d2},
		Layout:     domain.Known(CombatLayout{Firing: []domain.Cell{{X: 9, Z: 23}, {X: 8, Z: 23}, {X: 10, Z: 23}}, Toward: domain.North}),
		Pawns: []CombatPawnState{
			{ID: "a", Cell: domain.Known(domain.Cell{X: 1, Z: 30}), Stance: StanceIdle},
			{ID: "b", Cell: domain.Known(domain.Cell{X: 2, Z: 30}), Stance: StanceIdle},
			{ID: "c", Cell: domain.Known(domain.Cell{X: 3, Z: 30}), Stance: StanceIdle},
		},
		Orderable: []domain.PawnID{"a", "b", "c"},
	}
}

// decide runs DecideCombat and answers its geometry ask once, as the
// caller does.
func decideStop(t *testing.T, view CombatView, stop StopEvent, memory CombatMemory) ([]CombatOrder, CombatMemory) {
	t.Helper()
	orders, ask, next := DecideCombat(view, GeometryReply{}, stop, memory)
	if ask != nil {
		if len(orders) != 0 || !reflect.DeepEqual(next, memory) {
			t.Fatal("a geometry ask came with orders or a memory change")
		}
		var again *GeometryRequest
		orders, again, next = DecideCombat(view, GeometryReply{Answered: true}, stop, memory)
		if again != nil {
			t.Fatal("a second geometry round trip in one stop")
		}
	}
	return orders, next
}

// Formation holds the line: every rifleman to a firing cell, in id order.
func TestDecideCombatFormationHoldsTheLine(t *testing.T) {
	orders, memory := decideStop(t, holdView(), StopEvent{}, CombatMemory{})
	want := []CombatOrder{
		{Pawn: "a", Kind: OrderMove, Cell: domain.Cell{X: 9, Z: 23}, Reason: ReasonFormation},
		{Pawn: "b", Kind: OrderMove, Cell: domain.Cell{X: 8, Z: 23}, Reason: ReasonFormation},
		{Pawn: "c", Kind: OrderMove, Cell: domain.Cell{X: 10, Z: 23}, Reason: ReasonFormation},
	}
	if !reflect.DeepEqual(orders, want) || memory.Tactic != TacticHold || memory.Formed != 100 {
		t.Fatalf("%+v %+v", orders, memory)
	}
}

// Formation asks the game for covered cells behind the layout's line
// (#871); a defender the line has no room for takes the best proposal.
func TestDecideCombatFormationTakesProposedCover(t *testing.T) {
	view := holdView()
	layout, _ := view.Layout.Value()
	layout.Firing = layout.Firing[:2]
	view.Layout = domain.Known(layout)
	_, ask, _ := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	if ask == nil || ask.Propose != RoleCoverBehindLine || !reflect.DeepEqual(ask.Line, layout.Firing) || !reflect.DeepEqual(ask.Hostiles, []domain.PawnID{"r1", "r2"}) {
		t.Fatalf("%+v", ask)
	}
	orders, _, memory := DecideCombat(view, GeometryReply{Answered: true, Proposals: []domain.Cell{{X: 9, Z: 23}, {X: 11, Z: 24}}}, StopEvent{}, CombatMemory{})
	if memory.Tactic != TacticHold || len(orders) != 3 || orders[2].Cell != (domain.Cell{X: 11, Z: 24}) {
		t.Fatalf("%+v %+v", orders, memory)
	}
}

// The same input twice, and the same input shuffled, decide the same
// orders and memory.
func TestDecideCombatIsDeterministic(t *testing.T) {
	baseOrders, baseMemory := decideStop(t, holdView(), StopEvent{}, CombatMemory{})
	again, againMemory := decideStop(t, holdView(), StopEvent{}, CombatMemory{})
	if !reflect.DeepEqual(baseOrders, again) || !reflect.DeepEqual(baseMemory, againMemory) {
		t.Fatal("same input, different output")
	}
	rng := rand.New(rand.NewSource(852))
	for range 50 {
		v := holdView()
		rng.Shuffle(len(v.Defenders), func(i, j int) { v.Defenders[i], v.Defenders[j] = v.Defenders[j], v.Defenders[i] })
		rng.Shuffle(len(v.Threats), func(i, j int) { v.Threats[i], v.Threats[j] = v.Threats[j], v.Threats[i] })
		rng.Shuffle(len(v.Positional), func(i, j int) { v.Positional[i], v.Positional[j] = v.Positional[j], v.Positional[i] })
		rng.Shuffle(len(v.Pawns), func(i, j int) { v.Pawns[i], v.Pawns[j] = v.Pawns[j], v.Pawns[i] })
		rng.Shuffle(len(v.Orderable), func(i, j int) { v.Orderable[i], v.Orderable[j] = v.Orderable[j], v.Orderable[i] })
		orders, memory := decideStop(t, v, StopEvent{}, CombatMemory{})
		if !reflect.DeepEqual(baseOrders, orders) || !reflect.DeepEqual(baseMemory, memory) {
			t.Fatalf("shuffled input decided %+v, want %+v", orders, baseOrders)
		}
	}
}

// A steady fight emits no orders: pawns on their way, then in place and
// shooting, are left alone; only a pawn that stopped short is re-ordered.
func TestDecideCombatChangesOnly(t *testing.T) {
	view := holdView()
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	// Next stop: all three are walking to their cells.
	view.Tick = 160
	for i := range view.Pawns {
		view.Pawns[i].Stance = StanceMoving
	}
	if orders, next := decideStop(t, view, StopEvent{Kind: "entered_range"}, memory); len(orders) != 0 {
		t.Fatalf("a steady stop ordered %+v", orders)
	} else {
		memory = next
	}
	// In place and shooting whatever they chose: still nothing.
	view.Tick = 220
	cells := map[domain.PawnID]domain.Cell{"a": {X: 9, Z: 23}, "b": {X: 8, Z: 23}, "c": {X: 10, Z: 23}}
	for i := range view.Pawns {
		view.Pawns[i].Cell, view.Pawns[i].Target, view.Pawns[i].Stance = domain.Known(cells[view.Pawns[i].ID]), "r1", StanceCooldown
	}
	// c stopped short, idle away from its cell.
	view.Pawns[2].Cell, view.Pawns[2].Stance = domain.Known(domain.Cell{X: 10, Z: 26}), StanceIdle
	orders, _ := decideStop(t, view, StopEvent{}, memory)
	want := []CombatOrder{{Pawn: "c", Kind: OrderMove, Cell: domain.Cell{X: 10, Z: 23}, Reason: ReasonFormation}}
	if !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v", orders)
	}
}

// An order that would interrupt aim warmup or cooldown is dropped, and
// given at a later stop once the pawn is free; a pawn not held by the
// fight's own draft is never ordered.
func TestDecideCombatWarmupGuard(t *testing.T) {
	view := holdView()
	view.Pawns[0].Stance = StanceWarmup
	view.Pawns[1].Stance = StanceCooldown
	view.Orderable = []domain.PawnID{"a", "b"}
	orders, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	if len(orders) != 0 || len(memory.Issued) != 0 {
		t.Fatalf("orders through aim: %+v", orders)
	}
	view.Tick = 130
	view.Pawns[0].Stance = StanceIdle
	orders, _ = decideStop(t, view, StopEvent{}, memory)
	if len(orders) != 1 || orders[0].Pawn != "a" {
		t.Fatalf("%+v", orders)
	}
}

// A raider past the line re-forms the hold as squad defense on it.
func TestDecideCombatCompromisedHoldReformsAsSquad(t *testing.T) {
	view := holdView()
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	view.Positional[0].Position = domain.Known(domain.Cell{X: 9, Z: 24})
	view.Positional[0].NearestColonistDistance = domain.Known(1.0)
	orders, next := decideStop(t, view, StopEvent{Kind: "melee_contact"}, memory)
	if next.Tactic != TacticSquad || len(orders) == 0 {
		t.Fatalf("%+v %+v", orders, next)
	}
	for _, o := range orders {
		if o.Kind != OrderAttack {
			t.Fatalf("squad defense ordered %+v", o)
		}
	}
}
