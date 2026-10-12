package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// envelopeColony is a built 3x3 storage room (its 16-cell wall ring standing)
// whose planned vent is not built, its interior open to the sky on a
// bad-climate plan, plus a roofed hallway cell outside any room (#2962).
func envelopeColony(t *testing.T) (observation.ColonyProjection, domain.Cell, domain.Cell) {
	t.Helper()
	room := policy.PlannedRoom{ID: 1, Role: policy.PlannedStorage, Region: policy.RectRegion(policy.Rectangle{X: 10, Z: 10, Width: 3, Height: 3})}
	vent, hall := domain.Cell{X: 13, Z: 11}, domain.Cell{X: 30, Z: 30}
	plan := policy.LayoutPlan{
		Rooms: []policy.PlannedRoom{room},
		Sites: []policy.PlannedSite{{SitePlacement: policy.SitePlacement{Node: "vent-1", Origin: vent}, Kind: policy.NodeVent}},
	}
	plan.Roofed = policy.RegionOf(append(room.Region.Cells(), hall))
	var built []policy.CurrentBuilding
	var cells []policy.SiteCell
	for x := int32(9); x <= 13; x++ {
		for z := int32(9); z <= 13; z++ {
			c := domain.Cell{X: x, Z: z}
			if x > 9 && x < 13 && z > 9 && z < 13 {
				cells = append(cells, policy.SiteCell{Cell: c, Roofed: domain.Known(false)})
				continue
			}
			b, err := domain.NewBuilding(policy.ShellWallDefinition, c, domain.North, "BlocksGranite")
			if err != nil {
				t.Fatal(err)
			}
			built = append(built, policy.CurrentBuilding{ID: "w", Building: b, Cells: []domain.Cell{c}})
		}
	}
	cells = append(cells, policy.SiteCell{Cell: hall, Roofed: domain.Known(false)})
	facts := observation.ColonyProjection{Cells: cells, Definitions: []observation.PlanningDefinition{{Name: policy.ClimateDefs().VentDef, Available: domain.Known(true)}}}
	facts.LayoutPlan = domain.Known(plan)
	facts.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: built})
	return facts, vent, hall
}

func TestEnvelopeWorkBuildsABuiltRoomWithoutItsOwner(t *testing.T) {
	facts, _, hall := envelopeColony(t)
	rooms, hallway, known := envelopeWork(facts)
	if !known || len(rooms) != 1 || len(hallway) != 1 || hallway[0] != hall {
		t.Fatalf("rooms=%v hallway=%v known=%v", rooms, hallway, known)
	}
	if owed, _ := envelopeOwed(facts).Value(); !owed {
		t.Fatal("a built room missing its vent and roof owes nothing")
	}
	plan, _ := facts.LayoutPlan.Value()
	in, _ := roomRingInput(facts, plan, rooms[0])
	var vents, roofs int
	for _, op := range policy.EnvelopeOps(policy.ReconcileRoom(in)) {
		switch op.Kind {
		case policy.OpVentIn:
			vents += len(op.Cells)
		case policy.OpRoofIn:
			roofs += len(op.Cells)
		}
	}
	if vents != 1 || roofs != 9 {
		t.Fatalf("vents=%d roofs=%d, want 1 and 9", vents, roofs)
	}
}

func TestEnvelopeIdleWhenNothingIsMissing(t *testing.T) {
	facts, vent, _ := envelopeColony(t)
	// Roofed everywhere, and the vent stands in its wall.
	for i := range facts.Cells {
		facts.Cells[i].Roofed = domain.Known(true)
	}
	construction, _ := facts.Facts.CurrentConstruction.Value()
	for i, b := range construction.Buildings {
		if b.Cells[0] == vent {
			v, err := domain.NewBuilding(policy.ClimateDefs().VentDef, vent, domain.East, "")
			if err != nil {
				t.Fatal(err)
			}
			construction.Buildings[i].Building = v
		}
	}
	facts.Facts.CurrentConstruction = domain.Known(construction)
	if owed, known := envelopeOwed(facts).Value(); !known || owed {
		t.Fatalf("a complete envelope owes work: owed=%v known=%v", owed, known)
	}
}

func TestEnvelopeWaitsForRingVentAvailabilityAndFacts(t *testing.T) {
	facts, _, _ := envelopeColony(t)
	unavailable := facts
	unavailable.Definitions = []observation.PlanningDefinition{{Name: policy.ClimateDefs().VentDef, Available: domain.Known(false)}}
	// Only the roof and hallway remain; the unavailable vent adds nothing.
	rooms, _, _ := envelopeWork(unavailable)
	if len(rooms) != 1 {
		t.Fatalf("the roof is still owed: %v", rooms)
	}
	plan, _ := unavailable.LayoutPlan.Value()
	in, _ := roomRingInput(unavailable, plan, rooms[0])
	for _, op := range policy.EnvelopeOps(policy.ReconcileRoom(in)) {
		if op.Kind == policy.OpVentIn {
			t.Fatalf("an unavailable vent is owed: %+v", op)
		}
	}
	// No ring yet: neither a vent nor a roof is owed, so only the hallway is.
	bare := facts
	bare.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true})
	if rooms, hallway, known := envelopeWork(bare); !known || len(rooms) != 0 || len(hallway) != 1 {
		t.Fatalf("unbuilt ring: rooms=%v hallway=%v known=%v", rooms, hallway, known)
	}
	// An unread construction census is unknown, never idle.
	unread := facts
	unread.Facts.CurrentConstruction = domain.Unknown[policy.CurrentConstruction]()
	if _, known := envelopeOwed(unread).Value(); known {
		t.Fatal("unread census read as known")
	}
	// An outdoor room is never roofed.
	plan, _ = facts.LayoutPlan.Value()
	plan.Rooms = []policy.PlannedRoom{plan.Rooms[0]}
	plan.Rooms[0].Outdoor = true
	outdoor := facts
	outdoor.LayoutPlan = domain.Known(plan)
	if rooms, _, _ := envelopeWork(outdoor); len(rooms) != 0 {
		t.Fatalf("outdoor room owed %v", rooms)
	}
}
