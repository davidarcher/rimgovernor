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
		if ok || !refusal.Is(policy.CauseSiteBlocked) {
			t.Errorf("%s: %v %v", name, refusal, ok)
		}
	}
	// A row with no flammability is unread data, not a pass.
	bare := stuffRow("Wall", observation.StuffOption{Stuff: "Steel", Stats: map[string]float64{}})
	if _, _, refusal, ok := fireproofShellStuff(bare, door); ok || !refusal.Is(policy.CauseFieldUnavailable) {
		t.Fatal(refusal, ok)
	}
}

// A planned incinerator is owed its shell until every wall and the door
// stand, and is then the room the dump zone fills.
func TestIncineratorStepFollowsTheRing(t *testing.T) {
	room := policy.PlannedRoom{Role: policy.PlannedIncinerator, Interior: policy.Rectangle{X: 11, Z: 11, Width: 3, Height: 3}, Door: domain.Cell{X: 10, Z: 12}, DoorRot: domain.West}
	plan := policy.LayoutPlan{Rooms: []policy.PlannedRoom{room}}
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
	var built []policy.CurrentBuilding
	in := room.Interior
	var ring []domain.Cell
	for x := in.X - 1; x <= in.X+in.Width; x++ {
		for z := in.Z - 1; z <= in.Z+in.Height; z++ {
			if x == in.X-1 || x == in.X+in.Width || z == in.Z-1 || z == in.Z+in.Height {
				ring = append(ring, domain.Cell{X: x, Z: z})
			}
		}
	}
	for _, c := range ring {
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

// The batch is the interior cells holding things; a burn waits for a read,
// empty fire census.
func TestIncineratorBatchAndFireGate(t *testing.T) {
	interior := policy.Rectangle{X: 10, Z: 10, Width: 3, Height: 3}
	var facts observation.ColonyProjection
	for i, c := range policy.RectangleCells(interior) {
		facts.Cells = append(facts.Cells, policy.SiteCell{Cell: c, StorageEmpty: domain.Known(i >= 4)})
	}
	facts.Cells = append(facts.Cells, policy.SiteCell{Cell: domain.Cell{X: 20, Z: 20}, StorageEmpty: domain.Known(false)})
	if got := incineratorStored(facts, interior); got != 4 {
		t.Fatal(got)
	}
	if !fireBurning(facts) {
		t.Fatal("an unread fire census is not a quiet one")
	}
	facts.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{})
	if fireBurning(facts) {
		t.Fatal("an empty fire census burns")
	}
	facts.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{{ID: "f"}})
	if !fireBurning(facts) {
		t.Fatal("a fire is not burning")
	}
	got := molotovs([]bridge.EquipCandidate{{Thing: "m", Definition: policy.MolotovDef}, {Thing: "g", Definition: "Gun"}})
	if len(got) != 1 || got[0].Thing != "m" {
		t.Fatal(got)
	}
}
