package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestThroneRefuelOrdersUnlitEmptyBrazier(t *testing.T) {
	plan, room, need := throneFixture()
	need.AnyOfCounts = []ThingAnyOfCount{{Things: []string{"Brazier"}, Count: 2}}
	need.Glowing = [][]string{{"Brazier"}}
	standing := tombStanding(room)
	in := room.Interior
	inside := domain.Cell{X: in.X, Z: in.Z}
	outside := domain.Cell{X: in.X + in.Width + 5, Z: in.Z}
	hauler := WorkPawn{ID: "p1", Available: domain.Known(true), Applies: domain.Known(true),
		Work: domain.Known([]WorkPriority{{Work: WorkHauling, Priority: 2}})}
	lamp := func(id, def string, cell domain.Cell, lit bool, empty domain.Fact[bool]) Lamp {
		return Lamp{ID: id, Definition: def, Cell: cell, Lit: lit, OutOfFuel: empty}
	}
	empty, fuelled := domain.Known(true), domain.Known(false)
	step := NextThroneRefuel(plan, standing, need, []Lamp{
		lamp("b2", "Brazier", inside, false, empty),
		lamp("b1", "Brazier", inside, false, empty),
		lamp("lit", "Brazier", inside, true, fuelled),
		lamp("far", "Brazier", outside, false, empty),
		lamp("lamp", "StandingLamp", inside, false, empty),
	}, []WorkPawn{hauler})
	if step.Kind != ThroneRefuel || step.Lamp != "b1" || step.Pawn != "p1" || !step.Owed() {
		t.Fatalf("unlit empty brazier: %+v", step)
	}
	for name, lamps := range map[string][]Lamp{
		"lit":     {lamp("b", "Brazier", inside, true, fuelled)},
		"unknown": {lamp("b", "Brazier", inside, false, domain.Unknown[bool]())},
		"outside": {lamp("b", "Brazier", outside, false, empty)},
		"other":   {lamp("b", "StandingLamp", inside, false, empty)},
	} {
		if got := NextThroneRefuel(plan, standing, need, lamps, []WorkPawn{hauler}); got.Kind != ThroneNone {
			t.Fatalf("%s: %+v", name, got)
		}
	}
	if got := NextThroneRefuel(plan, standing, need, []Lamp{lamp("b", "Brazier", inside, false, empty)}, nil); got.Kind != ThroneNone {
		t.Fatalf("no hauler: %+v", got)
	}
}
