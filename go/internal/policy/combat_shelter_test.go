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
