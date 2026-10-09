package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func shelterTestView() CombatView {
	view := holdView()
	view.Layout = domain.Unknown[CombatLayout]()
	for i := range view.Defenders {
		view.Defenders[i].Armed, view.Defenders[i].RangedEquipped = domain.Known(false), domain.Known(false)
	}
	return view
}

func TestShelterUsesVerifiedRetreatAndNeverRepeatsRefusedCell(t *testing.T) {
	view := shelterTestView()
	good, refused := domain.Cell{X: 8, Z: 35}, domain.Cell{X: 9, Z: 35}
	view.Layout = domain.Known(CombatLayout{Retreat: []domain.Cell{refused, good}})
	_, ask, _ := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	if ask == nil {
		t.Fatal("missing standability read")
	}
	geometry := GeometryReply{Answered: true, Standable: []domain.Cell{refused, good}}
	orders, _, memory := DecideCombat(view, geometry, StopEvent{}, CombatMemory{})
	if len(orders) < 1 || orders[0].Cell != refused {
		t.Fatalf("retreat priority: %+v", orders)
	}
	memory = memory.RefuseCell(refused).Forget(orders[0].Pawn)
	view.Tick++
	orders, _, next := DecideCombat(view, geometry, StopEvent{}, memory)
	for _, order := range orders {
		if order.Kind == OrderMove && order.Cell == refused {
			t.Fatalf("refused cell reissued: %+v", order)
		}
	}
	if len(next.Roles) == 0 || *next.Roles[0].Cell != good {
		t.Fatalf("no alternate: %+v", next.Roles)
	}
	// The unchecked vector and all surrounding walls are omitted by native;
	// the sole standable interior retreat is still selected.
	orders, _, _ = DecideCombat(view, GeometryReply{Answered: true, Standable: []domain.Cell{good}}, StopEvent{}, CombatMemory{})
	if len(orders) == 0 || orders[0].Cell != good {
		t.Fatalf("wall replaced interior: %+v", orders)
	}
}

func TestShelterContinuationStableUnknownAndNewDefenders(t *testing.T) {
	view := shelterTestView()
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	for i := range view.Pawns {
		view.Pawns[i].Stance = StanceMoving
	}
	view.Tick++
	orders, next := decideStop(t, view, StopEvent{}, memory)
	if len(orders) != 0 || next.Formed != memory.Formed || !reflect.DeepEqual(next.Roles, memory.Roles) {
		t.Fatalf("stable shelter replaced: %+v %+v", orders, next)
	}
	view.Defenders[0].Armed = domain.Unknown[bool]()
	orders, next = decideStop(t, view, StopEvent{}, next)
	if len(orders) != 0 || next.Tactic != TacticShelter {
		t.Fatalf("unknown equipment replaced shelter: %+v", next)
	}
	for i := range view.Defenders {
		view.Defenders[i] = combatRifleman(view.Defenders[i].ID)
	}
	view.Tick++
	_, next = decideStop(t, view, StopEvent{}, next)
	if next.Tactic != TacticSquad {
		t.Fatalf("new defender kept shelter: %+v", next)
	}
}

func TestShelterReconsidersReachedExposedCell(t *testing.T) {
	view := shelterTestView()
	cell, _ := view.Pawns[0].Cell.Value()
	view.Positional[0].Position = domain.Known(domain.Cell{X: cell.X + 1, Z: cell.Z})
	memory := CombatMemory{Tactic: TacticShelter, Roles: []CombatRole{{Pawn: "a", Cell: &cell, Retreat: true}}}
	if ShelterReconsider(view, memory) != "shelter_exposed" {
		t.Fatal("arrival under threat did not reconsider")
	}
	view.Pawns[0].Cell = domain.Unknown[domain.Cell]()
	if ShelterReconsider(view, memory) != "" {
		t.Fatal("unknown arrival triggered replacement")
	}
}

// A fists-only colonist against a raider takes the roofed room with no
// hostile in it, or with none, steps straight away from the raider (#968).
func TestShelterPrefersARoofedRoomElseStepsAway(t *testing.T) {
	d := combatRifleman("a")
	d.RangedEquipped, d.Armed = domain.Known(false), domain.Known(false)
	s, p := combatRaider("r", domain.Cell{X: 0, Z: 10})
	view := CombatView{Tick: 1, Defenders: []SquadDefenderFacts{d}, Threats: []SquadThreatFacts{s}, Positional: []DefensiveThreatFacts{p}, Orderable: []domain.PawnID{"a"},
		Pawns: []CombatPawnState{{ID: "a", Cell: domain.Known(domain.Cell{X: 10, Z: 10}), Stance: StanceIdle}}}
	roofed := CombatRoom{Interior: Rectangle{X: 20, Z: 10, Width: 2, Height: 2}, Roofed: true}
	open := CombatRoom{Interior: Rectangle{X: 40, Z: 10, Width: 2, Height: 2}}
	raided := CombatRoom{Interior: Rectangle{X: -1, Z: 9, Width: 3, Height: 3}, Roofed: true}
	view.Rooms = []CombatRoom{open, raided, roofed}
	orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	if m.Tactic != TacticShelter || len(orders) != 1 || orders[0].Kind != OrderMove || !roofed.contains(orders[0].Cell) {
		t.Fatalf("%s %+v, want a move into the roofed room", m.Tactic, orders)
	}
	view.Rooms = nil
	orders, _ = decideStop(t, view, StopEvent{}, CombatMemory{})
	if len(orders) != 1 || orders[0].Cell.X < 10+shelterStep {
		t.Fatalf("%+v, want a step away from the raider", orders)
	}
}

// A fists-only colonist against a manhunter pack shelters too (#1146):
// the manhunter formation has no role for her, and a fight with no role
// admits no combat window, so the clock would park on unsafe_colony.
func TestShelterFromAManhunterPack(t *testing.T) {
	d := combatRifleman("a")
	d.RangedEquipped, d.Armed = domain.Known(false), domain.Known(false)
	view := CombatView{Tick: 1, Defenders: []SquadDefenderFacts{d}, Orderable: []domain.PawnID{"a"},
		Pawns: []CombatPawnState{{ID: "a", Cell: domain.Known(domain.Cell{X: 10, Z: 10}), Stance: StanceIdle}}}
	view = withAnimals(view, animal("w1", "Warg", domain.Cell{X: 0, Z: 10}, 6.6))
	if !ManhunterPack(view) {
		t.Fatal("not a manhunter pack")
	}
	orders, m := decideStop(t, view, StopEvent{}, CombatMemory{})
	if m.Tactic != TacticShelter || len(m.Roles) != 1 || len(orders) != 1 || orders[0].Cell.X < 10+shelterStep {
		t.Fatalf("%s %+v, want a step away from the pack", m.Tactic, orders)
	}
}

// The fallback move never leaves the walled compound the colonist stands in
// (#2376): with no roofed room free of hostiles, the step away from a gunner
// outside the east wall is clamped to the interior.
func TestShelterFallbackStaysInsideTheCompound(t *testing.T) {
	compound := CombatRoom{Interior: Rectangle{X: 41, Z: 37, Width: 19, Height: 19}}
	view := CombatView{Tick: 1, Rooms: []CombatRoom{compound}}
	for i, c := range []domain.Cell{{X: 45, Z: 46}, {X: 55, Z: 40}, {X: 58, Z: 50}} {
		id := domain.PawnID(string(rune('a' + i)))
		d := combatRifleman(id)
		d.RangedEquipped, d.Armed = domain.Known(false), domain.Known(false)
		view.Defenders = append(view.Defenders, d)
		view.Orderable = append(view.Orderable, id)
		view.Pawns = append(view.Pawns, CombatPawnState{ID: id, Cell: domain.Known(c), Stance: StanceIdle})
	}
	s, p := combatRaider("r", domain.Cell{X: 62, Z: 46})
	view.Threats, view.Positional = []SquadThreatFacts{s}, []DefensiveThreatFacts{p}
	orders, _ := decideStop(t, view, StopEvent{}, CombatMemory{})
	if len(orders) != 3 {
		t.Fatalf("%+v, want three moves", orders)
	}
	for _, o := range orders {
		if o.Kind != OrderMove || !compound.contains(o.Cell) {
			t.Fatalf("order %+v leaves the compound", o)
		}
	}
}
