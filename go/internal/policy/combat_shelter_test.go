package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

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
	orders, _, m := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	if m.Tactic != TacticShelter || len(orders) != 1 || orders[0].Kind != OrderMove || !roofed.contains(orders[0].Cell) {
		t.Fatalf("%s %+v, want a move into the roofed room", m.Tactic, orders)
	}
	view.Rooms = nil
	orders, _, _ = DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	if len(orders) != 1 || orders[0].Cell != (domain.Cell{X: 10 + shelterStep, Z: 10}) {
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
	orders, _, m := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	if m.Tactic != TacticShelter || len(m.Roles) != 1 || len(orders) != 1 || orders[0].Cell != (domain.Cell{X: 10 + shelterStep, Z: 10}) {
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
	orders, _, _ := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	if len(orders) != 3 {
		t.Fatalf("%+v, want three moves", orders)
	}
	for _, o := range orders {
		if o.Kind != OrderMove || !compound.contains(o.Cell) {
			t.Fatalf("order %+v leaves the compound", o)
		}
	}
}
