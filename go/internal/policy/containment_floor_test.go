package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestContainmentCellCountsTheBioferriteFloorWhenStockPaysForIt (#2435): the
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
