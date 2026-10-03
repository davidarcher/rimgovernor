package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// storageSlot is a 4x4-interior storage room at (11..14, 21..24), its door
// on the south wall at (12,20).
func storageSlot(t *testing.T) domain.RoomFootprint {
	t.Helper()
	var interior []domain.Cell
	for x := int32(11); x <= 14; x++ {
		for z := int32(21); z <= 24; z++ {
			interior = append(interior, domain.Cell{X: x, Z: z})
		}
	}
	room, err := domain.NewRoomFootprint(interior, domain.Cell{X: 12, Z: 20}, domain.South)
	if err != nil {
		t.Fatal(err)
	}
	return room
}

// #1186: the starter shell standing on the storage slot takes the covered
// stockpile inside it, beside the bunks and off the door aisle, and no new
// walls are raised.
func TestStorageRoomStepStockpilesInsideAStandingShell(t *testing.T) {
	room := storageSlot(t)
	var cells []policy.SiteCell
	for _, w := range room.Walls() {
		c := policy.SiteCell{Cell: w, PlayerEdifice: domain.Known("Wall"), Doorway: domain.Known(false)}
		if w == room.Door() {
			c = policy.SiteCell{Cell: w, PlayerEdifice: domain.Known(""), Doorway: domain.Known(true)}
		}
		cells = append(cells, c)
	}
	bunk := domain.Cell{X: 14, Z: 24}
	for _, in := range room.Interior() {
		cells = append(cells, policy.SiteCell{Cell: in, Roofed: domain.Known(true), Occupied: domain.Known(in == bunk), Zone: domain.Known(false)})
	}
	stockpile, perimeter := storageRoomStep(room, cells)
	if perimeter != nil {
		t.Fatal(perimeter)
	}
	got := map[domain.Cell]bool{}
	for _, c := range stockpile {
		got[c] = true
	}
	if len(stockpile) != 16-1-3 || got[bunk] || got[domain.Cell{X: 12, Z: 21}] || got[domain.Cell{X: 11, Z: 21}] || got[domain.Cell{X: 13, Z: 21}] || !got[domain.Cell{X: 14, Z: 21}] {
		t.Fatal(stockpile)
	}
}

// #1186: with no shell on the slot the step raises walls exactly on the
// storage room's ring, door first, and never an ad-hoc site.
func TestStorageRoomStepRaisesTheSlotRingWithoutAShell(t *testing.T) {
	room := storageSlot(t)
	stockpile, perimeter := storageRoomStep(room, nil)
	if stockpile != nil || len(perimeter) != len(room.Walls()) || perimeter[0] != room.Door() {
		t.Fatal(stockpile, perimeter)
	}
	ring := map[domain.Cell]bool{}
	for _, w := range room.Walls() {
		ring[w] = true
	}
	for _, c := range perimeter {
		if !ring[c] {
			t.Fatal(c)
		}
	}
}

func supplyRoomShellPlan(t *testing.T, id domain.PlanID, originX, originZ int32) domain.PlanSpec {
	t.Helper()
	door := domain.Cell{X: originX + 3, Z: originZ}
	var actions []domain.Action
	i := 0
	for x := originX; x < originX+6; x++ {
		for z := originZ; z < originZ+6; z++ {
			cell := domain.Cell{X: x, Z: z}
			if cell != door && (x != originX && x != originX+5 && z != originZ && z != originZ+5) {
				continue
			}
			def := "Wall"
			if cell == door {
				def = "Door"
			}
			b, err := domain.NewBuilding(def, cell, domain.North, "")
			if err != nil {
				t.Fatal(err)
			}
			a, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), b)
			if err != nil {
				t.Fatal(err)
			}
			actions = append(actions, a)
			i++
		}
	}
	spec, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	return spec
}

func TestSecureSuppliesRoomShellPlanRecognizesWallDoorShell(t *testing.T) {
	spec := supplyRoomShellPlan(t, "supply-room-shell-1", 10, 20)
	if !secureSuppliesRoomShellPlan(spec) {
		t.Fatal("expected a Wall/Door perimeter plan to be recognized as the room shell")
	}
}

func TestSecureSuppliesRoomShellPlanIgnoresUnrelatedPlans(t *testing.T) {
	b, err := domain.NewBuilding("SleepingSpot", domain.Cell{X: 1, Z: 1}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction("other-0", b)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("other", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if secureSuppliesRoomShellPlan(spec) {
		t.Fatal("expected an unrelated plan not to be recognized as the room shell")
	}
}

func TestSecureSuppliesRoomShellPlanIgnoresHaulAndZoneActions(t *testing.T) {
	haul, err := domain.NewHaul("pawn-1", "item-1", "Silver", domain.Cell{X: 2, Z: 2})
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewHaulAction("haul-0", haul)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("haul-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if secureSuppliesRoomShellPlan(spec) {
		t.Fatal("expected a haul plan not to be recognized as the room shell")
	}
}

func rockSiteCell(cell domain.Cell) policy.SiteCell {
	return policy.SiteCell{Cell: cell, Occupied: domain.Known(true), Walkable: domain.Known(false), Roof: domain.Known("RoofRockThick")}
}

// #1754: a storage room planned into rock digs its interior and door
// (dig, then build) and leaves ring walls on rock as the walls they are; a
// fogged cell, absent from the frame, is dug when it needs floor and left
// when it is a wall. Open ground needs nothing.
func TestSupplyRoomRockStepDigsFloorAndLeavesWalls(t *testing.T) {
	room := storageSlot(t)
	roles := supplyRoomRoleCells(room)
	var cells []policy.SiteCell
	for _, w := range room.Walls() {
		cells = append(cells, rockSiteCell(w))
	}
	for _, in := range room.Interior() {
		if in.X == 11 {
			continue // fogged: not listed
		}
		cells = append(cells, rockSiteCell(in))
	}
	step := policy.RockStep(roles, cells)
	dig := map[domain.Cell]bool{}
	for _, c := range step.Dig {
		dig[c] = true
	}
	if !dig[room.Door()] || !dig[domain.Cell{X: 12, Z: 22}] || !dig[domain.Cell{X: 11, Z: 22}] || len(step.Dig) != 1+len(room.Interior()) {
		t.Fatal(step.Dig)
	}
	if len(step.Left) != len(room.Walls())-1 {
		t.Fatal(step.Left)
	}
	if open := policy.RockStep(roles, nil); len(open.Dig) != 1+len(room.Interior()) {
		t.Fatal(open.Dig)
	}
	if done := policy.RockStep(roles, []policy.SiteCell{{Cell: room.Door(), Occupied: domain.Known(false), Walkable: domain.Known(true)}}); slices.Contains(done.Dig, room.Door()) {
		t.Fatal(done.Dig)
	}
}

// supplyRoomRockNative is the rock fixture as a SecureSupplies source.
type supplyRoomRockNative struct{ *rockCoolerNative }

func (supplyRoomRockNative) ReadTendPawns(context.Context, *c.Identity, []string) (*o.ListPawnsReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}

func (supplyRoomRockNative) PreviewZone(context.Context, *c.Identity, domain.ZoneCreate) (*op.ZonePreviewReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}

// #1754: supplyRoomFallback mines a storage room planned into rock, listed
// (visible) or fogged, as plan-dig-supply-room: the door and the ring walls
// still to build depend on every excavation, and the step waits on the dig.
func TestSupplyRoomFallbackAdmitsRockDigBeforeTheRing(t *testing.T) {
	t.Parallel()
	for _, fogged := range []bool{false, true} {
		name := "visible"
		if fogged {
			name = "fogged"
		}
		t.Run(name, func(t *testing.T) {
			t.Parallel()
			p, db, n, s, _, _ := rockCoolerStep(t)
			ctx := context.Background()
			room := storageSlot(t)
			layout := policy.LayoutPlan{Rooms: []policy.LayoutRoom{{Role: policy.ModuleStorage, Interior: policy.Rectangle{X: 11, Z: 21, Width: 4, Height: 4}, Door: room.Door(), DoorRot: domain.South}}}
			rockCells := append([]domain.Cell{room.Door()}, room.Interior()...)
			n.rock = map[domain.Cell]bool{}
			s.facts.Cells = nil
			for _, cell := range rockCells {
				n.rock[cell] = true
			}
			if !fogged {
				// Ring walls: the first two on rock, the rest open ground.
				for i, w := range room.Walls() {
					if w == room.Door() {
						continue
					}
					cell := openCell(w.X, w.Z)
					if i < 2 {
						n.rock[w] = true
						cell = rockSiteCell(w)
					}
					s.facts.Cells = append(s.facts.Cells, cell)
				}
				for _, cell := range rockCells {
					s.facts.Cells = append(s.facts.Cells, rockSiteCell(cell))
				}
			}
			s.facts.LayoutPlan = domain.Known(layout)
			s.facts.Definitions = []observation.PlanningDefinition{{Name: "Wall", Available: domain.Known(true)}, {Name: "Door", Available: domain.Known(true)}}
			planner := &RoutineSecureSuppliesPlanner{reviewer: p.reviewer, native: supplyRoomRockNative{n}}
			reading := observation.ColonyReading{Projection: s.facts, StartedAt: s.read.StartedAt}
			call, epoch, done, err := p.reviewer.player.enter(ctx, "test", false)
			if err != nil {
				t.Fatal(err)
			}
			defer done()
			result, err := planner.supplyRoomFallback(call, epoch, s.state, s.goal, s.review, reading, policy.UpkeepItem{Definition: "Steel"}, time.Now())
			if err != nil {
				t.Fatal(err)
			}
			if result.Kind != PlanWaiting || result.Dependency != "storage room dig" || result.Verdict != BuildingReasonAdmitted {
				t.Fatal(result)
			}
			goal, err := db.LoadGoal(ctx, s.goal.Goal.ID)
			if err != nil {
				t.Fatal(err)
			}
			var planID domain.PlanID
			for _, m := range goal.Methods {
				if matchesMethod(m.Method, "plan-dig-supply-room") {
					planID = m.Plan
				}
			}
			if planID == "" {
				t.Fatal("plan-dig-supply-room not admitted", goal.Methods)
			}
			plan, err := db.LoadPlan(ctx, planID)
			if err != nil {
				t.Fatal(err)
			}
			requires := map[domain.ActionID]bool{}
			built := map[domain.ActionID]bool{}
			for _, dep := range plan.Spec.Dependencies() {
				built[dep.Action] = true
				requires[dep.Requires] = true
			}
			dug := map[domain.Cell]bool{}
			var door, buildings int
			for _, action := range plan.Spec.Actions() {
				if b, ok := action.Building(); ok {
					buildings++
					if !built[action.ID()] {
						t.Fatal("building not waiting on the dig", b)
					}
					if b.Definition() == "Door" && b.Cell() == room.Door() {
						door++
					}
					continue
				}
				e, ok := action.Excavation()
				if !ok || !requires[action.ID()] {
					t.Fatal("excavation not required by the ring", action)
				}
				dug[e.Cell()] = true
			}
			if door != 1 || len(dug) != len(rockCells) {
				t.Fatal(door, dug)
			}
			wantWalls := 0
			if !fogged {
				wantWalls = len(room.Walls()) - 1 - 2
			}
			if buildings != 1+wantWalls {
				t.Fatal("ring buildings", buildings, "want", 1+wantWalls)
			}
		})
	}
}
