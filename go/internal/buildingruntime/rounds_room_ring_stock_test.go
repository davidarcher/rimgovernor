package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestStorageRingLeavesInteriorStockAndClearsRingObstructions(t *testing.T) {
	room := policy.PlannedRoom{Role: policy.PlannedStorage, Interior: policy.Rectangle{X: 10, Z: 10, Width: 3, Height: 3}}
	plan := policy.LayoutPlan{Rooms: []policy.PlannedRoom{room}}
	stock := policy.Thing{ID: 1, Def: "Meat_Gazelle", Category: policy.ThingItem, Flags: policy.FlagHaulable}
	ringStock := policy.Thing{ID: 2, Def: "Leather_Plain", Category: policy.ThingItem, Flags: policy.FlagHaulable}
	for _, obstructRing := range []bool{false, true} {
		t.Run(map[bool]string{false: "interior stock only", true: "ring stock too"}[obstructRing], func(t *testing.T) {
			facts := observation.ColonyProjection{Cells: []policy.SiteCell{{Cell: domain.Cell{X: 11, Z: 11}, Things: []policy.Thing{stock}}}}
			facts.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true})
			if obstructRing {
				facts.Cells = append(facts.Cells, policy.SiteCell{Cell: domain.Cell{X: 9, Z: 11}, Things: []policy.Thing{ringStock}})
			}
			in, known := roomRingInput(facts, plan, room)
			if !known {
				t.Fatal("known construction census lost")
			}
			walls, hauled := 0, 0
			for _, op := range policy.ReconcileRoom(in) {
				if op.Kind == policy.OpWallIn {
					walls += len(op.Cells)
				}
				if op.Kind == policy.OpHaulOut {
					for _, target := range op.Targets {
						if target.EntityID != ringStock.LoadID() {
							t.Fatalf("shell hauls interior stock: %+v", target)
						}
						hauled++
					}
				}
			}
			wantWalls, wantHauled := 16, 0
			if obstructRing {
				wantWalls, wantHauled = 15, 1
			}
			if walls != wantWalls || hauled != wantHauled {
				t.Fatalf("walls=%d hauled=%d, want %d/%d", walls, hauled, wantWalls, wantHauled)
			}
			if len(facts.Cells[0].Things) != 1 {
				t.Fatal("ring input changed the shared observation")
			}
		})
	}
}
