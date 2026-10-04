package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// penMountain is a 32x32 map whose middle (8..24 each way) is natural rock
// and the rest open ground: a pen has no open 6x6 near the anchor, so it is
// sited in the mountain. fogged leaves the rock unlisted.
func penMountain(t *testing.T, fogged bool) (*RoutineAnimalContainmentPlanner, excavationStep, *rockCoolerNative) {
	t.Helper()
	p, _, n, s, _, _ := rockCoolerStep(t)
	s.facts.Bounds = policy.Bounds{Width: 32, Height: 32}
	s.facts.Center = domain.Cell{X: 16, Z: 16}
	s.facts.Cells = nil
	n.rock = map[domain.Cell]bool{}
	for x := int32(0); x < 32; x++ {
		for z := int32(0); z < 32; z++ {
			cell := domain.Cell{X: x, Z: z}
			site := policy.SiteCell{Cell: cell, Occupied: domain.Known(false), Walkable: domain.Known(true), Zone: domain.Known(false), SupportsLight: domain.Known(true), Roof: domain.Known("")}
			if x >= 8 && x <= 24 && z >= 8 && z <= 24 {
				n.rock[cell] = true
				if fogged {
					continue
				}
				site.Occupied, site.Walkable, site.Roof, site.NaturalRock = domain.Known(true), domain.Known(false), domain.Known("RoofRockThick"), domain.Known(true)
			}
			s.facts.Cells = append(s.facts.Cells, site)
		}
	}
	return &RoutineAnimalContainmentPlanner{reviewer: p.reviewer, native: n, building: p}, s, n
}

// A pen sited in rock keeps the rock ring, mines the gate cell and the
// interior, and builds the gate once every excavation is done, in one shell
// method; fogged mountain is sited and mined the same way.
func TestPenShellOnRockMinesInteriorAndGateThenBuildsGate(t *testing.T) {
	for _, fogged := range []bool{false, true} {
		name := "listed rock"
		if fogged {
			name = "fogged rock"
		}
		t.Run(name, func(t *testing.T) {
			t.Parallel()
			r, s, n := penMountain(t, fogged)
			call, epoch, done, err := r.reviewer.player.enter(context.Background(), "test", false)
			if err != nil {
				t.Fatal(err)
			}
			defer done()
			ctx := context.Background()
			result, err := r.digShell(call, epoch, s.state, s.review, s.goal.(store.StandardState), s.facts, nil, observation.RoutineReading{ColonyReading: s.read}, "")
			if err != nil || result.Verdict != BuildingReasonAdmitted || result.Plan == "" {
				t.Fatal(result, err)
			}
			if n.overRock != 1 || len(n.overCells) != 1 {
				t.Fatal("the gate on rock previews over rock once", n.overRock, n.overCells)
			}
			plan, err := r.reviewer.player.journal.LoadPlan(ctx, result.Plan)
			if err != nil {
				t.Fatal(err)
			}
			kind, room := animalContainmentPlanKindOf(plan.Spec)
			if kind != animalContainmentPlanShell || room.Width != policy.PenEnclosureSize || room.Z != 8 {
				t.Fatal(kind, room)
			}
			gate := domain.Cell{X: room.X + room.Width/2, Z: room.Z}
			if n.overCells[0] != gate {
				t.Fatal(n.overCells, gate)
			}
			var buildings, dug int
			for _, action := range plan.Spec.Actions() {
				if b, ok := action.Building(); ok {
					buildings++
					if b.Definition() != "FenceGate" || b.Cell() != gate {
						t.Fatal("ring cells on rock stay rock; only the gate is built", b)
					}
				}
				if e, ok := action.Excavation(); ok {
					dug++
					inside := e.Cell().X > room.X && e.Cell().X < room.X+room.Width-1 && e.Cell().Z > room.Z && e.Cell().Z < room.Z+room.Height-1
					if !inside && e.Cell() != gate {
						t.Fatal("dug outside the gate and interior", e.Cell())
					}
				}
			}
			if buildings != 1 || dug != 17 {
				t.Fatal(buildings, dug)
			}
		})
	}
}

// The pen shell's room comes from its gate, so a ring that is mostly rock
// (and so has few fences) still recovers the whole 6x6 for the marker.
func TestAnimalContainmentPlanKindOfRecoversRoomFromGateAlone(t *testing.T) {
	t.Parallel()
	b, err := domain.NewBuilding("FenceGate", domain.Cell{X: 13, Z: 8}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction("gate-0", b)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("gate", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	kind, room := animalContainmentPlanKindOf(spec)
	if kind != animalContainmentPlanShell || room != (policy.Rectangle{X: 10, Z: 8, Width: 6, Height: 6}) {
		t.Fatal(kind, room)
	}
}
