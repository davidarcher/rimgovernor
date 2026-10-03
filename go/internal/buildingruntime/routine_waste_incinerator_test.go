package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func stuffRow(name string, options ...observation.StuffOption) observation.PlanningDefinition {
	return observation.PlanningDefinition{Name: name, Stuffed: true, StuffOptions: options}
}

func burning(stuff string, flammability float64) observation.StuffOption {
	return observation.StuffOption{Stuff: stuff, Costs: []policy.Amount{{Resource: policy.Resource(stuff), Count: 5}}, Value: 1, Stats: map[string]float64{bridge.StatFlammability: flammability}}
}

// The incinerator's walls and door take the least flammable stuff each def
// offers, and a flammable wall or door is refused, never built.
func TestFireproofShellStuffRejectsFlammable(t *testing.T) {
	wall := stuffRow("Wall", burning("WoodLog", 1), burning("BlocksGranite", 0))
	door := stuffRow("Door", burning("WoodLog", 1), burning("Steel", 0))
	w, d, _, ok := fireproofShellStuff(wall, door)
	if !ok || w != "BlocksGranite" || d != "Steel" {
		t.Fatal(w, d, ok)
	}
	for name, pair := range map[string][2]observation.PlanningDefinition{
		"wooden wall": {stuffRow("Wall", burning("WoodLog", 1)), door},
		"wooden door": {wall, stuffRow("Door", burning("WoodLog", 1))},
	} {
		_, _, refusal, ok := fireproofShellStuff(pair[0], pair[1])
		if ok || !refusal.Is(RefusalSiteBlocked) {
			t.Errorf("%s: %v %v", name, refusal, ok)
		}
	}
	// A row with no flammability is unread data, not a pass.
	bare := stuffRow("Wall", observation.StuffOption{Stuff: "Steel", Stats: map[string]float64{}})
	if _, _, refusal, ok := fireproofShellStuff(bare, door); ok || !refusal.Is(RefusalFieldUnavailable) {
		t.Fatal(refusal, ok)
	}
}

// A planned incinerator is owed its shell until every wall and the door
// stand, and is then the room the dump zone fills.
func TestIncineratorStepFollowsTheRing(t *testing.T) {
	site := policy.IncineratorSite{Area: policy.Rectangle{X: 10, Z: 10, Width: 5, Height: 5}, Facing: domain.North}
	plan := policy.LayoutPlan{Reservations: []policy.LayoutReservation{site.Reservation()}}
	facts := observation.ColonyProjection{LayoutPlan: domain.Known(plan)}
	if owed, known := incineratorOwed(facts).Value(); known || owed {
		t.Fatal("owed before the construction census is read")
	}
	facts.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true})
	if owed, known := incineratorOwed(facts).Value(); !known || !owed {
		t.Fatal("not owed with no walls standing")
	}
	if standingIncinerator(facts) != nil {
		t.Fatal("standing with no walls")
	}
	room := site.Room()
	var built []policy.CurrentBuilding
	for _, c := range incineratorRing(plan, room, facts) {
		def := "Wall"
		if c == room.Door {
			def = "Door"
		}
		b, err := domain.NewBuilding(def, c, domain.North, "BlocksGranite")
		if err != nil {
			t.Fatal(err)
		}
		built = append(built, policy.CurrentBuilding{ID: def, Building: b, Cells: []domain.Cell{c}})
	}
	if len(built) != 16 {
		t.Fatal("a 3x3 interior has a ring of 16", len(built))
	}
	facts.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: built})
	if owed, known := incineratorOwed(facts).Value(); !known || owed {
		t.Fatal("owed with the ring standing")
	}
	if got := standingIncinerator(facts); got == nil || got.Interior != room.Interior {
		t.Fatal("ring standing but not the dump zone's room", got)
	}
}
