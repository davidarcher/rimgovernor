package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A Camp-tier tribe whose five colonists own spots in the starter shell,
// with Bed locked, owes a bedroom and shells one (#1182): spots are never
// suitable, so the sleeping choice is unavailable and the bedroom ladder
// answers it ahead of any barracks bed.
func TestTribalSpotOwnersShellABedroom(t *testing.T) {
	plan := policy.LayoutPlan{Rooms: []policy.PlannedRoom{
		{Role: policy.PlannedShelter, Interior: policy.Rectangle{X: 0, Z: 0, Width: 9, Height: 7}, DoorRot: domain.North},
		{Role: policy.PlannedBedroom, Interior: policy.Rectangle{X: 10, Z: 0, Width: 5, Height: 5}, Door: domain.Cell{X: 12, Z: 5}, DoorRot: domain.North},
	}}
	sleeping := policy.SleepingObservation{Colonists: 5, BedBuildable: domain.Known(false)}
	shell := policy.Room{ID: "shell", Role: domain.Known(policy.RoomRoleBarracks), Enclosed: domain.Known(true), Cells: []domain.Cell{{X: 4, Z: 3}}}
	for _, id := range []string{"a", "b", "c", "d", "e"} {
		bed := "spot-" + id
		sleeping.People = append(sleeping.People, policy.SleepingPerson{ID: policy.PawnID(id), OwnedBed: domain.Known(bed)})
		sleeping.Beds = append(sleeping.Beds, policy.SleepingBed{ID: bed, Definition: policy.SleepingSpotDefinition, Owners: []policy.PawnID{policy.PawnID(id)}})
		shell.Beds = append(shell.Beds, bed)
	}
	facts := observation.ColonyProjection{
		TechTier:   domain.Known(policy.TechTierCamp),
		LayoutPlan: domain.Known(plan),
		Rooms:      domain.Known(policy.RoomObservation{Shapes: testPieceShapes, Rooms: []policy.Room{shell}}),
	}
	facts.Facts.Sleeping = domain.Known(sleeping)
	if step := bedroomStep(facts, policy.StageReserves); step.Kind != policy.BedroomReconcile || step.Unhoused != 5 {
		t.Fatalf("step = %+v, want the bedroom shelled for 5", step)
	}
	if owed, known := bedroomsOwed(facts, policy.StageReserves).Value(); !known || !owed {
		t.Fatal("bedroom deficit not owed at Camp tier")
	}
	if !bedroomsFirst(policy.SleepingUnavailable) || !bedroomsFirst(policy.SleepingBuild) || bedroomsFirst(policy.SleepingAssign) {
		t.Fatal("bedrooms must come before barracks beds, never before an assignment")
	}
}
