package layout

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/planstage"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func rect(x, z, w, h int32) policy.Region {
	return policy.RectRegion(policy.Rectangle{X: x, Z: z, Width: w, Height: h})
}

// envelopePlan is a kitchen (3x3 at 0,0) with a door east at (3,1) into a
// bedroom (3x3 at 4,0), the kitchen's exterior door south at (1,-1) opening
// into an airlock vestibule at (1,-2) whose outer door is (1,-3), and an
// outdoor yard.
func envelopePlan() policy.LayoutPlan {
	roofed := policy.RegionOf(append(append(rect(0, 0, 3, 3).Cells(), rect(4, 0, 3, 3).Cells()...), domain.Cell{X: 1, Z: -2}))
	return policy.LayoutPlan{
		Roofed: roofed,
		Links:  []policy.RoomLink{{A: 1, B: 2, Cell: domain.Cell{X: 3, Z: 1}}},
		Rooms: []policy.PlannedRoom{
			{ID: 1, Role: policy.PlannedKitchen, Region: rect(0, 0, 3, 3), Door: domain.Cell{X: 1, Z: -1}, DoorRot: domain.South},
			{ID: 2, Role: policy.PlannedBedroom, Region: rect(4, 0, 3, 3), Door: domain.Cell{X: 5, Z: 3}, DoorRot: domain.North},
			{ID: 3, Role: policy.PlannedAirlock, Parent: 1, Region: rect(1, -2, 1, 1), Door: domain.Cell{X: 1, Z: -3}, DoorRot: domain.South},
			{ID: 4, Role: policy.PlannedYard, Region: rect(10, 10, 4, 4), Door: domain.Cell{X: 10, Z: 9}},
		},
	}
}

func TestEnvelopePlanHolds(t *testing.T) {
	plan := envelopePlan()
	if err := unroofedRooms(plan); err != nil {
		t.Errorf("unroofedRooms: %v", err)
	}
	if err := airlockCount(plan); err != nil {
		t.Errorf("airlockCount: %v", err)
	}
	if err := disconnectedRooms(plan); err != nil {
		t.Errorf("disconnectedRooms: %v", err)
	}
}

func TestEnvelopeFlagsAnUnroofedRoom(t *testing.T) {
	plan := envelopePlan()
	plan.Roofed = policy.RegionOf(rect(0, 0, 3, 3).Cells())
	if err := unroofedRooms(plan); err == nil || !strings.Contains(err.Error(), "bedroom") {
		t.Fatalf("unroofedRooms = %v, want the bedroom named", err)
	}
}

func TestEnvelopeFlagsARoomOnlyJoinedOutside(t *testing.T) {
	plan := envelopePlan()
	plan.Links = nil // the bedroom's only door leads out of the roofed set
	err := disconnectedRooms(plan)
	if err == nil || !strings.Contains(err.Error(), "bedroom room 2") {
		t.Fatalf("disconnectedRooms = %v, want the bedroom unjoined", err)
	}
}

func TestEnvelopeNeedsAnAirlock(t *testing.T) {
	plan := envelopePlan()
	plan.Rooms = plan.Rooms[:2]
	if airlockCount(plan) == nil {
		t.Fatal("a plan without an airlock passed")
	}
}

func audit(cells map[domain.Cell]planstage.CellRead) planstage.Audit {
	out := planstage.Audit{Cells: map[planstage.Cell]planstage.CellRead{}}
	for c, read := range cells {
		out.Cells[planstage.Cell{X: c.X, Z: c.Z}] = read
	}
	return out
}

func TestEnvelopeReadsTheBuiltGround(t *testing.T) {
	plan := envelopePlan()
	built := map[domain.Cell]planstage.CellRead{}
	for _, c := range plan.Roofed.Cells() {
		built[c] = planstage.CellRead{Roofed: true}
	}
	built[domain.Cell{X: 1, Z: -1}] = planstage.CellRead{Edifice: "Door"}
	built[domain.Cell{X: 1, Z: -3}] = planstage.CellRead{Edifice: "Door"}
	built[domain.Cell{X: 0, Z: -2}] = planstage.CellRead{Edifice: "Wall"}
	built[domain.Cell{X: 2, Z: -2}] = planstage.CellRead{Edifice: "Wall"}
	a := audit(built)
	if got := unbuiltRoofs(plan, a); len(got) != 0 {
		t.Errorf("unbuiltRoofs = %v, want none", got)
	}
	if got := airlockFaults(plan, a); len(got) != 0 {
		t.Errorf("airlockFaults = %v, want none", got)
	}

	built[domain.Cell{X: 5, Z: 1}] = planstage.CellRead{}
	built[domain.Cell{X: 2, Z: -2}] = planstage.CellRead{Edifice: "Frame_Wall"}
	a = audit(built)
	if got := unbuiltRoofs(plan, a); len(got) != 1 || got[0] != (domain.Cell{X: 5, Z: 1}) {
		t.Errorf("unbuiltRoofs = %v, want the bedroom cell 5,1", got)
	}
	if got := airlockFaults(plan, a); len(got) != 1 || !strings.Contains(got[0], "side wall") {
		t.Errorf("airlockFaults = %v, want the frame wall", got)
	}
}

func TestEnvelopeAuditReadsEveryRoofedAndAirlockCell(t *testing.T) {
	plan := envelopePlan()
	cells := map[planstage.Cell]bool{}
	for _, c := range envelopeCells(plan) {
		cells[c] = true
	}
	for _, want := range append(plan.Roofed.Cells(), domain.Cell{X: 1, Z: -3}, domain.Cell{X: 0, Z: -2}, domain.Cell{X: 2, Z: -2}, domain.Cell{X: 1, Z: -1}) {
		if !cells[planstage.Cell{X: want.X, Z: want.Z}] {
			t.Errorf("cell %v is not audited", want)
		}
	}
}

func TestSeaIceEnvelopeIsRegisteredAndLints(t *testing.T) {
	c, ok := cases.Lookup("layout/sea-ice-envelope")
	if !ok {
		t.Fatal("layout/sea-ice-envelope is not registered")
	}
	if err := c.Lint(); err != nil {
		t.Fatal(err)
	}
}
