package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// ringConstruction is the colony's construction census with room's wall ring
// standing as planned (walls all round, a door at its Door), or empty.
func ringConstruction(room *policy.PlannedRoom) domain.Fact[policy.CurrentConstruction] {
	census := policy.CurrentConstruction{Colony: true}
	if room != nil {
		in := room.Interior
		for x := in.X - 1; x <= in.X+in.Width; x++ {
			for z := in.Z - 1; z <= in.Z+in.Height; z++ {
				if x != in.X-1 && x != in.X+in.Width && z != in.Z-1 && z != in.Z+in.Height {
					continue
				}
				cell, def := domain.Cell{X: x, Z: z}, "Wall"
				if cell == room.Door {
					def = "Door"
				}
				b, err := domain.NewBuilding(def, cell, domain.North, "")
				if err != nil {
					panic(err)
				}
				census.Buildings = append(census.Buildings, policy.CurrentBuilding{Building: b, Cells: []domain.Cell{cell}})
			}
		}
	}
	return domain.Known(census)
}

func TestPlannedRoomOwedAtAnyTierUntilTheRoomStands(t *testing.T) {
	kitchen := policy.PlannedRoom{Role: policy.PlannedKitchen, Interior: policy.Rectangle{X: 10, Z: 10, Width: 6, Height: 5}, Door: domain.Cell{X: 12, Z: 9}}
	facts := observation.ColonyProjection{LayoutPlan: domain.Known(policy.LayoutPlan{Rooms: []policy.PlannedRoom{kitchen}}), Rooms: domain.Known(policy.RoomObservation{Shapes: testPieceShapes}), TechTier: domain.Known(policy.TechTierCamp)}
	facts.Facts.CurrentConstruction = ringConstruction(nil)
	for _, tier := range []policy.TechTier{policy.TechTierCamp, policy.TechTierMasonry} {
		facts.TechTier = domain.Known(tier)
		if r, owed := plannedRoomOwed(facts, policy.PlannedKitchen); !owed || r.Interior != kitchen.Interior {
			t.Fatal("the planned kitchen is not owed at tier", tier, r, owed)
		}
	}
	if plannedRoomCells(facts, policy.PlannedKitchen) != nil {
		t.Fatal("an unbuilt kitchen restricted the stove")
	}
	facts.Rooms = domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{{ID: "k", Cells: []domain.Cell{{X: 13, Z: 12}}, Enclosed: domain.Known(true)}}})
	facts.Facts.CurrentConstruction = ringConstruction(&kitchen)
	if _, owed := plannedRoomOwed(facts, policy.PlannedKitchen); owed {
		t.Fatal("a standing kitchen is still owed")
	}
	if cells := plannedRoomCells(facts, policy.PlannedKitchen); len(cells) != 30 {
		t.Fatal("the stove is not held to the kitchen interior", len(cells))
	}
}

func TestPlannedRoomInteriorIsTheWholeRoom(t *testing.T) {
	room := policy.PlannedRoom{Role: policy.PlannedKitchen, Interior: policy.Rectangle{X: 10, Z: 10, Width: 6, Height: 5}}
	if cells := plannedRoomInterior(room); len(cells) != 30 || cells[0] != (domain.Cell{X: 10, Z: 10}) || cells[29] != (domain.Cell{X: 15, Z: 14}) {
		t.Fatal(cells)
	}
}

// Each facility's furniture goes in the layout plan's room of its role
// (#2267): into the interior of the standing room, with no census room
// hosting it. Without a planned room of the role, or a facility, the planner
// is left alone.
func TestPlannedFacilityFurnishingUsesThePlannedRoom(t *testing.T) {
	t.Parallel()
	step := func(r *RoundsBuildingPlanner, facts observation.ColonyProjection) (*RoundsBuildingPlanner, bool, error) {
		got, _, done, err := r.plannedFacilityFurnishing(context.TODO(), context.TODO(), ControlState{}, store.Rounds{}, store.StandardState{}, observation.ColonyReading{}, facts)
		return got, done, err
	}
	for _, tc := range []struct {
		role    policy.RoomRole
		planned policy.PlannedRole
	}{
		{policy.RoomRoleDiningRoom, policy.PlannedDining},
		{policy.RoomRoleRecRoom, policy.PlannedRec},
		{policy.RoomRoleHospital, policy.PlannedHospital},
		{policy.RoomRoleLaboratory, policy.PlannedLab},
		{policy.RoomRoleWorkshop, policy.PlannedWorkshop},
	} {
		facility, err := policy.Facility(tc.role)
		if err != nil {
			t.Fatal(err)
		}
		room := policy.PlannedRoom{Role: tc.planned, Interior: policy.Rectangle{X: 10, Z: 10, Width: 6, Height: 5}, Door: domain.Cell{X: 12, Z: 9}}
		facts := observation.ColonyProjection{LayoutPlan: domain.Known(policy.LayoutPlan{Rooms: []policy.PlannedRoom{room}}), TechTier: domain.Known(policy.TechTierCamp)}
		facts.Rooms = domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{{ID: "r", Cells: []domain.Cell{{X: 13, Z: 12}}, Enclosed: domain.Known(true)}}})
		facts.Facts.CurrentConstruction = ringConstruction(&room)
		r := &RoundsBuildingPlanner{facility: &facility, definition: "Furniture"}
		got, done, err := step(r, facts)
		if err != nil || done || got == r || got.facility != nil || len(got.cells) != 30 {
			t.Errorf("%s: furniture did not land in its planned room: done=%v err=%v got=%+v", tc.role, done, err, got)
		}
		bare := observation.ColonyProjection{LayoutPlan: domain.Known(policy.LayoutPlan{}), Rooms: facts.Rooms, TechTier: facts.TechTier}
		bare.Facts.CurrentConstruction = ringConstruction(nil)
		if got, done, err := step(r, bare); err != nil || done || got != r {
			t.Errorf("%s: planner changed with no planned room: done=%v err=%v", tc.role, done, err)
		}
	}
	plain := &RoundsBuildingPlanner{definition: "Table1x2c"}
	if got, done, err := step(plain, observation.ColonyProjection{}); err != nil || done || got != plain {
		t.Errorf("no facility: planner changed: done=%v err=%v", done, err)
	}
}
