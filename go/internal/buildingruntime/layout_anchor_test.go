package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// anchorProjection is an open 96x96 map at the tier; every cell is observed open ground.
func anchorProjection(tier policy.BuildTier) observation.ColonyProjection {
	p := observation.ColonyProjection{BuildTier: domain.Known(tier), Bounds: policy.Bounds{Width: 96, Height: 96}}
	for x := int32(0); x < 96; x++ {
		for z := int32(0); z < 96; z++ {
			p.Cells = append(p.Cells, policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false)})
		}
	}
	return p
}

// centrePlan is a plan whose centre is c: one hallway cell there.
func centrePlan(c domain.Cell) policy.LayoutPlan {
	return policy.LayoutPlan{Spine: []policy.SpineSegment{{From: c, To: c}}}
}

// centreOn records a plan centred on c, as the journal holds one for a
// planned colony: the fixtures' colonies are planned, so nothing waits on a
// plan. The plan is a single containment cell, a role no fixture plans for.
func centreOn(r *Rounder, c domain.Cell) {
	plan := policy.LayoutPlan{Rooms: []policy.LayoutRoom{{Role: policy.ModuleContainmentCell, Interior: policy.Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1}, DoorRot: domain.South}}}
	if err := r.player.journal.RecordLayoutPlan(context.Background(), r.player.session.State().Snapshot, 0, plan); err != nil {
		panic(err)
	}
}

func anchorPlan() policy.LayoutPlan {
	return policy.LayoutPlan{
		Rooms: []policy.LayoutRoom{
			{Role: policy.ModuleShelter, Interior: policy.Rectangle{X: 10, Z: 10, Width: 5, Height: 5}},
			{Role: policy.ModuleShelter, Interior: policy.Rectangle{X: 20, Z: 10, Width: 5, Height: 5}},
			{Role: policy.ModuleStorage, Interior: policy.Rectangle{X: 30, Z: 10, Width: 4, Height: 4}},
		},
		Zones: []policy.LayoutZone{{Kind: policy.ZoneField, Runs: []policy.RowRun{{Z: 70, X: 60, Length: 10}}}},
	}
}

func TestRoomAnchorIsTheNearestFreeRoomOfTheRole(t *testing.T) {
	p := anchorProjection(policy.BuildTierMasonry)
	p.LayoutPlan = domain.Known(anchorPlan())
	if c, _ := roomAnchor(p, policy.ModuleShelter, domain.Cell{X: 40, Z: 12}); c != (domain.Cell{X: 22, Z: 12}) {
		t.Fatalf("nearest to the east %v", c)
	}
	if c, _ := roomAnchor(p, policy.ModuleShelter, domain.Cell{X: 0, Z: 12}); c != (domain.Cell{X: 12, Z: 12}) {
		t.Fatalf("nearest to the west %v", c)
	}
	// A wall in the nearest barracks makes the other one the nearest free room.
	wall, err := domain.NewBuilding("Wall", domain.Cell{X: 21, Z: 11}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	p.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "w", Building: wall, Cells: []domain.Cell{{X: 21, Z: 11}}}}})
	if c, _ := roomAnchor(p, policy.ModuleShelter, domain.Cell{X: 40, Z: 12}); c != (domain.Cell{X: 12, Z: 12}) {
		t.Fatalf("occupied room not skipped %v", c)
	}
	// No workshop and no reserve room: the plan's centre anchors it.
	centre, _ := p.Center().Value()
	if c, _ := roomAnchor(p, policy.ModuleWorkshop, centre); c != centre {
		t.Fatalf("workshop %v", c)
	}
}

func TestRoomAnchorFallsBackToReserveRooms(t *testing.T) {
	p := anchorProjection(policy.BuildTierMasonry)
	plan := anchorPlan()
	plan.Rooms = append(plan.Rooms, policy.LayoutRoom{Role: policy.ModuleReserve, Interior: policy.Rectangle{X: 50, Z: 50, Width: 4, Height: 4}})
	p.LayoutPlan = domain.Known(plan)
	if c, _ := roomAnchor(p, policy.ModuleWorkshop, domain.Cell{}); c != (domain.Cell{X: 52, Z: 52}) {
		t.Fatalf("reserve fallback %v", c)
	}
}

func TestFieldAnchorReadsTheFieldZone(t *testing.T) {
	p := anchorProjection(policy.BuildTierMasonry)
	p.LayoutPlan = domain.Known(anchorPlan())
	if c, _ := fieldAnchor(p); c != (domain.Cell{X: 65, Z: 70}) {
		t.Fatalf("fields %v", c)
	}
	// Camp reads the plan too: pens, barn and turbines must not stack on
	// the colony centre.
	p.BuildTier = domain.Known(policy.BuildTierCamp)
	if c, _ := fieldAnchor(p); c != (domain.Cell{X: 65, Z: 70}) {
		t.Fatalf("camp fields %v", c)
	}
}

func TestAnchorsWaitForAPlan(t *testing.T) {
	p := anchorProjection(policy.BuildTierMasonry)
	if _, ok := fieldAnchor(p); ok {
		t.Fatal("a field anchor without a plan")
	}
	if _, ok := roomAnchor(p, policy.ModuleShelter, domain.Cell{X: 1, Z: 1}); ok {
		t.Fatal("a room anchor without a plan")
	}
	if _, ok := p.Center().Value(); ok {
		t.Fatal("a colony centre without a plan")
	}
}

func TestModuleRoleOfInvertsRoomRoles(t *testing.T) {
	for _, role := range []policy.RoomRole{policy.RoomRoleShelter, policy.RoomRoleWorkshop, policy.RoomRoleStoreroom} {
		m, ok := policy.ModuleRoleOf(role)
		if !ok {
			t.Fatalf("%v has no module", role)
		}
		if role == policy.RoomRoleStoreroom && m != policy.ModuleStorage {
			t.Fatalf("storeroom is %v", m)
		}
	}
	// Barracks is the census role only: nothing plans one (#2045).
	if _, ok := policy.ModuleRoleOf(policy.RoomRoleBarracks); ok {
		t.Fatal("the census Barracks role has a planned module")
	}
	if _, ok := policy.ModuleRoleOf(policy.RoomRoleCeremonialChamber); ok {
		t.Fatal("an unplanned role has a module")
	}
}
