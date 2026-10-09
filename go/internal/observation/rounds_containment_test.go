package observation

import (
	"slices"
	"testing"

	"google.golang.org/protobuf/proto"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func entityRow(min float64, held, capturable bool) *o.PawnState {
	return &o.PawnState{Dead: proto.Bool(false), Anomaly: &o.PawnAnomaly{
		Entity: proto.Bool(true), MinContainmentStrength: proto.Float64(min),
		Held: &o.HeldState{Held: proto.Bool(held), CanBeCaptured: proto.Bool(capturable)},
	}}
}

// TestContainmentDemandCountsCapturableUnheldEntities: only a living
// entity the game lets the colony capture that no platform holds asks for a
// cell, at the strength the most demanding needs; an unread fact makes the
// whole demand unknown.
func TestContainmentDemandCountsCapturableUnheldEntities(t *testing.T) {
	colonist := &o.PawnState{Dead: proto.Bool(false), Anomaly: &o.PawnAnomaly{Entity: proto.Bool(false)}}
	dead := entityRow(500, false, true)
	dead.Dead = proto.Bool(true)
	noTarget := &o.PawnState{Dead: proto.Bool(false), Anomaly: &o.PawnAnomaly{Entity: proto.Bool(true)}}
	rows := []*o.PawnState{colonist, dead, noTarget, entityRow(300, true, true), entityRow(400, false, false), entityRow(60, false, true), entityRow(120, false, true), {}}
	got, ok := containmentDemand(slices.Values(rows)).Value()
	if !ok || got.Entities != 2 || got.Required != 120 {
		t.Fatalf("%+v %v", got, ok)
	}
	if got, ok := containmentDemand(slices.Values([]*o.PawnState{colonist})).Value(); !ok || got.Entities != 0 {
		t.Fatalf("no entity: %+v %v", got, ok)
	}
	unread := entityRow(0, false, true)
	unread.Anomaly.MinContainmentStrength = nil
	if _, ok := containmentDemand(slices.Values([]*o.PawnState{entityRow(60, false, true), unread})).Value(); ok {
		t.Fatal("an entity whose needed strength is unread must leave the demand unknown")
	}
	if _, ok := containmentDemand(slices.Values([]*o.PawnState{{Anomaly: &o.PawnAnomaly{Entity: proto.Bool(true)}}})).Value(); ok {
		t.Fatal("an entity with no dead fact must leave the demand unknown")
	}
}

func platformRow(strength float64, available bool) *o.BuildingState {
	return &o.BuildingState{Anomaly: &o.AnomalyBuilding{Holder: &o.EntityHolderState{ContainmentStrength: proto.Float64(strength), Available: proto.Bool(available)}}}
}

// TestBuiltHoldersCarryTheHeldPawnAndDoors: the held pawn and the
// room's doors ride the holder into the planning facts.
func TestBuiltHoldersCarryTheHeldPawnAndDoors(t *testing.T) {
	row := platformRow(180, false)
	row.Anomaly.Holder.HeldPawn = &c.Ref{Id: proto.String("Thing_Fleshbeast1")}
	row.Anomaly.Holder.Doors = []*o.AnomalyDoor{{Cell: &c.Cell{X: proto.Int32(4), Z: proto.Int32(5)}, Open: proto.Bool(true), HoldOpen: proto.Bool(true),
		ContainmentBreached: proto.Bool(false), BlockedOpen: proto.Bool(false)}}
	got, ok := builtHolders(slices.Values([]*o.BuildingState{row})).Value()
	if !ok || len(got) != 1 || got[0].HeldPawn != "Thing_Fleshbeast1" {
		t.Fatalf("%+v %v", got, ok)
	}
	doors, known := got[0].Doors.Value()
	if !known || len(doors) != 1 || doors[0].Cell.X != 4 || doors[0].Cell.Z != 5 {
		t.Fatalf("%+v %v", doors, known)
	}
	if hold, _ := doors[0].HoldOpen.Value(); !hold {
		t.Fatal("hold_open lost")
	}
	// The planner closes that door.
	p := policy.ContainmentPlanning{Holders: domain.Known(got)}
	if cell, ok := policy.ContainmentDoorTarget(p); !ok || cell.X != 4 || cell.Z != 5 {
		t.Fatalf("%v %v", cell, ok)
	}
}

func TestBuiltHoldersAreTheNativeStrengthAndAvailability(t *testing.T) {
	got, ok := builtHolders(slices.Values([]*o.BuildingState{{}, platformRow(180, true), {Anomaly: &o.AnomalyBuilding{}}, platformRow(90, false)})).Value()
	if !ok || len(got) != 2 || got[0].Strength != 180 || !got[0].Available || got[1].Strength != 90 || got[1].Available {
		t.Fatalf("%+v %v", got, ok)
	}
	unread := platformRow(1, true)
	unread.Anomaly.Holder.Available = nil
	if _, ok := builtHolders(slices.Values([]*o.BuildingState{unread})).Value(); ok {
		t.Fatal("a holder with an unread fact must leave the list unknown")
	}
	if got, ok := builtHolders(slices.Values([]*o.BuildingState{{}})).Value(); !ok || len(got) != 0 {
		t.Fatalf("no holder: %+v %v", got, ok)
	}
}
