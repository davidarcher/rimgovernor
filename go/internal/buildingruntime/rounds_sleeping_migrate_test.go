package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A pending bedroom migration keeps housing owed though policy
// BedroomsOwed counts no deficit.
func TestPendingMigrationKeepsHousingOwed(t *testing.T) {
	room := func(id, x, replaces int32) policy.PlannedRoom {
		return policy.PlannedRoom{ID: id, Replaces: replaces, Role: policy.PlannedBedroom, Region: policy.RectRegion(policy.Rectangle{X: x, Z: 0, Width: 4, Height: 4})}
	}
	plan := policy.LayoutPlan{Rooms: []policy.PlannedRoom{room(1, 0, 0), room(2, 20, 1)}}
	standing := func(id string, x int32, beds ...string) policy.Room {
		return policy.Room{ID: id, Role: domain.Known(policy.RoomRoleBedroom), Enclosed: domain.Known(true), Beds: beds, Cells: []domain.Cell{{X: x + 2, Z: 2}}}
	}
	sleeping := policy.SleepingObservation{Colonists: 1,
		People: []policy.SleepingPerson{{ID: "a", OwnedBed: domain.Known("ob1")}},
		Beds:   []policy.SleepingBed{{ID: "ob1", Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: []policy.PawnID{"a"}}}}
	facts := observation.ColonyProjection{
		LayoutPlan: domain.Known(plan),
		Rooms:      domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{standing("o1", 0, "ob1"), standing("n1", 20)}}),
	}
	facts.Facts.Sleeping = domain.Known(sleeping)
	if step := migrateStep(facts); step.Kind != policy.BedroomReconcile || step.Room.Region.Bounds().X != 20 {
		t.Fatalf("migrate step = %+v, want the active room furnished", step)
	}
	if owed, known := bedroomsOwed(facts, policy.StageReserves).Value(); !known || !owed {
		t.Fatal("pending migration not owed")
	}
}
