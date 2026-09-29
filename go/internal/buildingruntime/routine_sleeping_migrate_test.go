package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A pending wing migration keeps housing owed (#1244) though policy
// BedroomsOwed counts no deficit.
func TestPendingMigrationKeepsHousingOwed(t *testing.T) {
	room := func(x int32) policy.LayoutRoom {
		return policy.LayoutRoom{Role: policy.ModuleBedroom, Interior: policy.Rectangle{X: x, Z: 0, Width: 4, Height: 4}}
	}
	plan := policy.LayoutPlan{Wings: []policy.Wing{
		{Purpose: policy.WingBedroomsRetiring, Rooms: []policy.LayoutRoom{room(0)}},
		{Purpose: policy.WingBedrooms, Rooms: []policy.LayoutRoom{room(20)}},
	}}
	standing := func(id string, x int32, beds ...string) policy.Room {
		return policy.Room{ID: id, Role: domain.Known(policy.RoomRoleBedroom), Enclosed: domain.Known(true), Beds: beds, Cells: []domain.Cell{{X: x + 2, Z: 2}}}
	}
	sleeping := policy.SleepingObservation{Colonists: 1,
		People: []policy.SleepingPerson{{ID: "a", OwnedBed: domain.Known("ob1")}},
		Beds:   []policy.SleepingBed{{ID: "ob1", Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: []policy.PawnID{"a"}}}}
	facts := observation.ColonyProjection{
		LayoutPlan: domain.Known(plan),
		Rooms:      domain.Known(policy.RoomObservation{Rooms: []policy.Room{standing("o1", 0, "ob1"), standing("n1", 20)}}),
	}
	facts.Facts.Sleeping = domain.Known(sleeping)
	if step := migrateStep(facts); step.Kind != policy.BedroomFurnish || step.Room.Interior.X != 20 {
		t.Fatalf("migrate step = %+v, want the active room furnished", step)
	}
	if owed, known := bedroomsOwed(facts).Value(); !known || !owed {
		t.Fatal("pending migration not owed")
	}
}