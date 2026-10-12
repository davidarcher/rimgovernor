package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// climateFixture is reconFixture with a planned vent in the east wall, the
// room's footprint roofed and every interior cell open to the sky (#2962).
func climateFixture() (ReconcileInput, domain.Cell) {
	in, room := reconFixture()
	vent := domain.Cell{X: 13, Z: 11}
	in.Plan.Sites = []PlannedSite{{SitePlacement: SitePlacement{Node: "vent-1", Origin: vent}, Kind: NodeVent}}
	in.Plan.Roofed = RectRegion(Rectangle{X: 9, Z: 9, Width: 5, Height: 5})
	in.VentAvailable = true
	in.Unroofed = map[domain.Cell]bool{}
	for _, c := range room.Region.Cells() {
		in.Unroofed[c] = true
	}
	return in, vent
}

func TestReconcileVentOverStandingWall(t *testing.T) {
	in, vent := climateFixture()
	op := readyOp(t, Reconcile(in), OpVentIn)
	if len(op.Cells) != 1 || op.Cells[0] != vent {
		t.Fatalf("vent op = %+v", op)
	}
	in.VentAvailable = false
	if op := readyOp(t, Reconcile(in), OpVentIn); op.Kind != "" {
		t.Fatalf("an unavailable vent is owed nothing: %+v", op)
	}
	in.VentAvailable = true
	in.Ground.vents = map[domain.Cell]bool{vent: true}
	if op := readyOp(t, Reconcile(in), OpVentIn); op.Kind != "" {
		t.Fatalf("a standing vent is owed no build: %+v", op)
	}
	in.Ground.vents = nil
	in.Ground.rock = map[domain.Cell]bool{vent: true}
	if op := readyOp(t, Reconcile(in), OpVentIn); op.Kind != "" {
		t.Fatalf("rock takes no vent: %+v", op)
	}
}

func TestReconcileVentStandingRowIsKept(t *testing.T) {
	in, vent := climateFixture()
	in.Ground.vents = map[domain.Cell]bool{vent: true}
	in.Rows = []ClearanceTarget{{EntityID: "v", DefName: climateTable.VentDef, Minimum: vent, Maximum: vent, Deconstructible: true, InHome: true, Player: true}}
	rec := Reconcile(in)
	for _, op := range rec.Owed {
		for _, tg := range op.Targets {
			if tg.EntityID == "v" {
				t.Fatalf("a planned vent is removed: %+v", op)
			}
		}
	}
}

func TestReconcileRoofsTheRoofedInteriorOnceTheRingStands(t *testing.T) {
	in, _ := climateFixture()
	op := readyOp(t, Reconcile(in), OpRoofIn)
	if len(op.Cells) != 9 {
		t.Fatalf("roof op = %+v", op)
	}
	bare := in
	bare.Ground = GroundCensus{}
	if op := readyOp(t, Reconcile(bare), OpRoofIn); op.Kind != "" {
		t.Fatalf("roof before the ring: %+v", op)
	}
	temperate := in
	temperate.Plan.Roofed = Region{}
	if op := readyOp(t, Reconcile(temperate), OpRoofIn); op.Kind != "" {
		t.Fatalf("temperate plan roofs: %+v", op)
	}
	done := in
	done.Unroofed = nil
	if op := readyOp(t, Reconcile(done), OpRoofIn); op.Kind != "" {
		t.Fatalf("roofed room owes a roof: %+v", op)
	}
	out := in
	out.Room.Outdoor = true
	if op := readyOp(t, Reconcile(out), OpRoofIn); op.Kind != "" {
		t.Fatalf("outdoor room roofs: %+v", op)
	}
}

func TestReconcileRoofsTheAirlockVestibule(t *testing.T) {
	in, _ := climateFixture()
	plan := in.Plan
	plan.Rooms = []PlannedRoom{in.Room}
	plan.Rooms[0].ID = 1
	air := attachAirlock(in.Room.Door, in.Room.DoorRot)
	air.Parent = 1
	air.ID = 2
	plan.Rooms = append(plan.Rooms, air)
	v := air.Vestibule()
	plan.Roofed = RegionOf(append(plan.Roofed.Cells(), v))
	in.Plan, in.Room = plan, plan.Rooms[0]
	in.Ground = ringWalls(plan, in.Room)
	for _, c := range air.AirlockWalls() {
		in.Ground.walls[c] = true
	}
	if in.Ground.doors == nil {
		in.Ground.doors = map[domain.Cell]bool{}
	}
	in.Ground.doors[air.Door] = true
	in.Unroofed[v] = true
	op := readyOp(t, Reconcile(in), OpRoofIn)
	found := false
	for _, c := range op.Cells {
		found = found || c == v
	}
	if !found {
		t.Fatalf("vestibule %v not roofed: %+v", v, op)
	}
}
