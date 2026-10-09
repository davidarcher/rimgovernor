package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestContainmentCellCountsTheBioferriteFloorWhenStockPaysForIt: the
// floor term takes the containment floor only when the stock covers every
// tile of the room, the capture verdict flips with it, and WantedFloors lays
// the same floor under the same test.
func TestContainmentCellCountsTheBioferriteFloorWhenStockPaysForIt(t *testing.T) {
	defs := platformDefs()
	defs.FloorStrength = 1
	defs.Floor = ContainmentFloor{Def: "BioferritePlate", Strength: 16}
	plain, err := defs.Predict(ContainmentRoom{})
	if err != nil {
		t.Fatal(err)
	}
	floored, err := defs.Predict(ContainmentRoom{Floored: true})
	if err != nil || floored-plain != 15 {
		t.Fatalf("floor adds %v (%v), want 15", floored-plain, err)
	}
	if _, err := platformDefs().Predict(ContainmentRoom{Floored: true}); err == nil {
		t.Fatal("predicted a floor the catalog does not have")
	}
	shape, _ := ChildRoomNeed{Module: PlannedContainmentCell, Furniture: []ChildFurniture{{Defs: []string{"HoldingPlatform"}, Count: 1}}}.shape(platformFurniture())
	size := ChildRoomSizes(shape)[0]
	tiles := int64(size[0] * size[1])
	floors := func(stock int64, available bool) FlooringFacts {
		return FlooringFacts{
			Definitions: map[string]FloorDefinition{"BioferritePlate": {Available: domain.Known(available), Terrain: domain.Known(true), Costs: domain.Known([]Amount{{Resource: "Bioferrite", Count: 4}})}},
			Stock:       domain.Known(map[Resource]int64{"Bioferrite": stock}), ContainmentFloor: defs.Floor,
		}
	}
	// An entity whose need plus margin lies between the plain and the floored
	// strength is owed a cell only with the floor.
	margin, _ := CaptureMargin(defs)
	p := planning(1, plain+7-margin)
	p.Defs = domain.Known(defs)
	unread := floors(4*tiles, true)
	unread.Stock = domain.Unknown[map[Resource]int64]()
	for name, c := range map[string]struct {
		f    FlooringFacts
		owed bool
	}{
		"stock pays":     {floors(4*tiles, true), true},
		"one tile short": {floors(4*tiles-1, true), false},
		"not researched": {floors(4*tiles, false), false},
		"unread stock":   {unread, false},
	} {
		if _, v := ContainmentCellNeed(p, platformFurniture(), c.f); v.Owed != c.owed {
			t.Fatalf("%s: %+v", name, v)
		}
	}
	room := PlannedRoom{Role: PlannedContainmentCell, Interior: Rectangle{Width: size[0], Height: size[1]}}
	cell := domain.Cell{}
	policy := DefaultFlooringPolicy()
	if got := WantedFloors(room, nil, floors(4*tiles, true), policy)(cell); got != "BioferritePlate" {
		t.Fatalf("wanted %q", got)
	}
	if got := WantedFloors(room, nil, floors(4*tiles-1, true), policy)(cell); got != "" {
		t.Fatalf("unaffordable floor wanted %q", got)
	}
	if FloorKept(room, nil, floors(4*tiles, true), policy)("WoodPlankFloor", "BioferritePlate") {
		t.Fatal("a plain floor stood in for the containment floor")
	}
}
func TestContainmentCellRetainsFloorAfterStockSpent(t *testing.T) {
	defs := platformDefs()
	defs.FloorStrength = 1
	defs.Floor = ContainmentFloor{Def: "BioferritePlate", Strength: 16}
	plain, _ := defs.Predict(ContainmentRoom{})
	margin, _ := CaptureMargin(defs)
	p := planning(1, plain+7-margin)
	p.Defs = domain.Known(defs)
	shape, _ := ChildRoomNeed{Module: PlannedContainmentCell, Furniture: []ChildFurniture{{Defs: []string{"HoldingPlatform"}, Count: 1}}}.shape(platformFurniture())
	size := ChildRoomSizes(shape)[0]
	room := PlannedRoom{Role: PlannedContainmentCell, Interior: Rectangle{X: 10, Z: 20, Width: size[0], Height: size[1]}}
	cells := rectCells(room.Interior)
	for _, tc := range []struct {
		name                        string
		laid, pending               int
		stock                       int64
		unknown, outside, replacing bool
		owed                        bool
	}{
		{name: "stock before orders", stock: int64(len(cells)) * 4, owed: true},
		{name: "all blueprinted stock spent", pending: len(cells), owed: true},
		{name: "all laid stock spent", laid: len(cells), owed: true},
		{name: "mixed construction", laid: 2, pending: len(cells) - 3, stock: 4, owed: true},
		{name: "unfinished unfunded tile", pending: len(cells) - 1},
		{name: "unread floors", pending: len(cells), unknown: true},
		{name: "unrelated floor", pending: len(cells), outside: true},
		{name: "replacement ordered", laid: len(cells), replacing: true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			observed := FloorRoom{}
			for i, cell := range cells {
				if tc.outside {
					cell.X += 100
				}
				row := FloorCell{Cell: cell, Terrain: "Soil"}
				if i < tc.laid {
					row.Terrain = defs.Floor.Def
				}
				if i >= tc.laid && i < tc.laid+tc.pending {
					row.Pending = defs.Floor.Def
				}
				if tc.replacing {
					row.Pending = "WoodPlankFloor"
				}
				observed.Cells = append(observed.Cells, row)
			}
			floors := FlooringFacts{
				Definitions: map[string]FloorDefinition{defs.Floor.Def: {Available: domain.Known(true), Terrain: domain.Known(true), Costs: domain.Known([]Amount{{Resource: "Bioferrite", Count: 4}})}},
				Stock:       domain.Known(map[Resource]int64{"Bioferrite": tc.stock}), ContainmentFloor: defs.Floor,
				Layout:      domain.Known(LayoutPlan{Rooms: []PlannedRoom{room}}),
				Observation: domain.Known(FlooringObservation{Rooms: []FloorRoom{observed}}),
			}
			if tc.unknown {
				floors.Observation = domain.Unknown[FlooringObservation]()
			}
			_, verdict := ContainmentCellNeed(p, platformFurniture(), floors)
			if verdict.Owed != tc.owed {
				t.Fatalf("verdict %+v, want owed %v", verdict, tc.owed)
			}
			wanted := WantedFloors(room, nil, floors, DefaultFlooringPolicy())(cells[0])
			if (wanted == defs.Floor.Def) != tc.owed {
				t.Fatalf("wanted %q, want containment floor %v", wanted, tc.owed)
			}
			if tc.owed && tc.stock == 0 {
				floors.Stock = domain.Unknown[map[Resource]int64]()
				if _, v := ContainmentCellNeed(p, platformFurniture(), floors); !v.Owed {
					t.Fatalf("fully covered floor needs no stock read: %+v", v)
				}
			}
		})
	}
}
