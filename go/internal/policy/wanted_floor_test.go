package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func wantedFloorFacts() FlooringFacts {
	facts := flooringDefinitions()
	facts.Stock = domain.Known(map[Resource]int64{"WoodLog": 100, "Steel": 100, "BlocksGranite": 100})
	facts.Definitions["TileGranite"] = FloorDefinition{Available: domain.Known(true), Terrain: domain.Known(true), Cleanliness: domain.Known(0.0), Beauty: domain.Known(2.0), Flammability: domain.Known(0.0), PathCost: domain.Known[int32](0), Costs: domain.Known([]Amount{{Resource: "BlocksGranite", Count: 2}}), Tags: []string{"FineFloor"}}
	return facts
}

func wantedFloorRoom(role PlannedRole) PlannedRoom {
	return PlannedRoom{Role: role, Interior: Rectangle{X: 10, Z: 10, Width: 3, Height: 2}}
}

func TestWantedFloorsThroneRoomTakesTheTitleTaggedFloor(t *testing.T) {
	want := WantedFloors(wantedFloorRoom(PlannedThrone), []string{"FineFloor"}, wantedFloorFacts(), flooringPolicy())
	for _, c := range rectCells(wantedFloorRoom(PlannedThrone).Interior) {
		if got := want(c); got != "TileGranite" {
			t.Fatal(c, got)
		}
	}
	if got := want(domain.Cell{X: 0, Z: 0}); got != "" {
		t.Fatal("a cell outside the room wants a floor", got)
	}
	// A title that asks no floor leaves the throne room unfloored.
	if got := WantedFloors(wantedFloorRoom(PlannedThrone), nil, wantedFloorFacts(), flooringPolicy())(domain.Cell{X: 10, Z: 10}); got != "" {
		t.Fatal(got)
	}
}

func TestWantedFloorsOrdinaryRoomTakesTheEconomicsChoice(t *testing.T) {
	p := flooringPolicy()
	facts := wantedFloorFacts()
	// The choice is the selector's: the same floor the review would lay.
	review, _ := ReviewFlooring(domain.Known(flooringCensus()), domain.Unknown[RoomObservation](), nil, p)
	proposal, err := SelectFlooringMethod(review, flooringDefinitions(), p)
	if err != nil || proposal.Tier != FloorTierClean {
		t.Fatal(proposal, err)
	}
	room := wantedFloorRoom(PlannedKitchen)
	got := WantedFloors(room, nil, flooringDefinitions(), p)(domain.Cell{X: 11, Z: 11})
	if got != proposal.Definition || got != "WoodPlankFloor" {
		t.Fatal(got, proposal.Definition)
	}
	// With steel the clean tier prefers sterile tile, and a living room stays on wood.
	facts.Stock = domain.Known(map[Resource]int64{"WoodLog": 100, "Steel": 100})
	if got := WantedFloors(room, nil, facts, p)(domain.Cell{X: 10, Z: 10}); got != "SterileTile" {
		t.Fatal(got)
	}
	if got := WantedFloors(wantedFloorRoom(PlannedBedroom), nil, facts, p)(domain.Cell{X: 10, Z: 10}); got == "" {
		t.Fatal("a bedroom wants a floor")
	}
}

func TestWantedFloorsNoneWithoutAnAffordableFloor(t *testing.T) {
	p := flooringPolicy()
	facts := wantedFloorFacts()
	facts.Stock = domain.Known(map[Resource]int64{})
	cell := domain.Cell{X: 10, Z: 10}
	if got := WantedFloors(wantedFloorRoom(PlannedKitchen), nil, facts, p)(cell); got != "" {
		t.Fatal(got)
	}
	if got := WantedFloors(wantedFloorRoom(PlannedThrone), []string{"FineFloor"}, facts, p)(cell); got != "" {
		t.Fatal(got)
	}
	// A role the flooring review never floors wants none.
	if got := WantedFloors(wantedFloorRoom(PlannedStorage), nil, wantedFloorFacts(), p)(cell); got != "" {
		t.Fatal(got)
	}
}
