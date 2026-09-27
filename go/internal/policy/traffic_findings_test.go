package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestTrafficFindingsFlagBedroomThoroughfare(t *testing.T) {
	v := FlooringObservation{Rooms: []FloorRoom{flooringRoom("bed", RoomRoleBedroom, "WoodPlankFloor", 10, 10), flooringRoom("kitchen", RoomRoleKitchen, "SterileTile", 20, 20)}, Terrains: flooringTerrains()}
	v.Traffic = []TrafficCell{
		{Cell: domain.Cell{X: 11, Z: 10}, Layer: TrafficColonist, Samples: 400, Terrain: "WoodPlankFloor", Home: true},
		{Cell: domain.Cell{X: 12, Z: 10}, Layer: TrafficColonist, Samples: 200, Terrain: "WoodPlankFloor", Home: true},
		{Cell: domain.Cell{X: 21, Z: 20}, Layer: TrafficColonist, Samples: 40, Terrain: "SterileTile", Home: true},
		{Cell: domain.Cell{X: 21, Z: 21}, Layer: TrafficAnimal, Samples: 25, Terrain: "SterileTile", Home: true},
		{Cell: domain.Cell{X: 5, Z: 5}, Layer: TrafficColonist, Samples: 900, Terrain: "Soil", Home: true},
	}
	got := TrafficFindings(v)
	want := []TrafficFinding{
		{Kind: TrafficThoroughfare, Room: "bed", Role: RoomRoleBedroom, Layer: TrafficColonist, Steps: 400},
		{Kind: TrafficAnimalInCleanRoom, Room: "kitchen", Role: RoomRoleKitchen, Layer: TrafficAnimal, Steps: 25},
	}
	if len(got) != len(want) || got[0] != want[0] || got[1] != want[1] {
		t.Fatalf("findings %+v, want %+v", got, want)
	}
	// A resident walking to bed is no thoroughfare.
	v.Traffic[0].Samples, v.Traffic[1].Samples = 60, 60
	v.Traffic = v.Traffic[:3]
	if got := TrafficFindings(v); len(got) != 0 {
		t.Fatalf("quiet bedroom flagged: %+v", got)
	}
}

func TestFlooringEntryTierMatsKitchenDoorCrossing(t *testing.T) {
	p := flooringPolicy()
	terrains := flooringTerrains()
	terrains["StrawMatting"] = FloorTerrain{Cleanliness: -0.1, Beauty: -1, PathCost: 1}
	kitchen := flooringRoom("kitchen", RoomRoleKitchen, "SterileTile", 10, 10)
	v := FlooringObservation{Rooms: []FloorRoom{kitchen}, Terrains: terrains}
	v.Traffic = []TrafficCell{
		{Cell: domain.Cell{X: 40, Z: 40}, Layer: TrafficCrossing, Samples: 90, Terrain: "WoodPlankFloor", Home: true},
		{Cell: domain.Cell{X: 10, Z: 10}, Layer: TrafficCrossing, Samples: 50, Terrain: "SterileTile", Home: true},
		{Cell: domain.Cell{X: 30, Z: 30}, Layer: TrafficCrossing, Samples: 5, Terrain: "WoodPlankFloor", Home: true},
		{Cell: domain.Cell{X: 10, Z: 10}, Layer: TrafficColonist, Samples: 500, Terrain: "SterileTile", Home: true},
	}
	r, err := ReviewFlooring(domain.Known(v), domain.Unknown[RoomObservation](), nil, p)
	if err != nil || len(r.Deficits) != 1 {
		t.Fatal(r, err)
	}
	// The kitchen door comes before the busier crossing elsewhere; the
	// quiet one is not an entry point.
	d := r.Deficits[0]
	if d.Tier != FloorTierEntry || len(d.Cells) != 2 || d.Cells[0] != (domain.Cell{X: 10, Z: 10}) || d.Cells[1] != (domain.Cell{X: 40, Z: 40}) {
		t.Fatal(d)
	}
	facts := flooringDefinitions()
	facts.Definitions["StrawMatting"] = FloorDefinition{Available: domain.Known(true), Terrain: domain.Known(true), Cleanliness: domain.Known(-0.1), Beauty: domain.Known(-1.0), Flammability: domain.Known(1.5), PathCost: domain.Known[int32](1), Costs: domain.Known([]Amount{{Resource: "Hay", Count: 2}})}
	facts.Stock = domain.Known(map[Resource]int64{"WoodLog": 100, "Hay": 40})
	proposal, err := SelectFlooringMethod(r, facts, p)
	if err != nil || proposal.Method != FlooringBuild || proposal.Tier != FloorTierEntry || proposal.Definition != "StrawMatting" || proposal.Cells[0] != (domain.Cell{X: 10, Z: 10}) {
		t.Fatal(proposal, err)
	}
	// Once matted, the kitchen cell is neither an entry point nor a clean
	// deficit despite the mat's negative cleanliness.
	v.Rooms[0].Cells[0].Terrain = "StrawMatting"
	v.Traffic[1].Terrain = "StrawMatting"
	v.Traffic[3].Terrain = "StrawMatting"
	r, err = ReviewFlooring(domain.Known(v), domain.Unknown[RoomObservation](), nil, p)
	if err != nil || len(r.Deficits) != 1 || r.Deficits[0].Tier != FloorTierEntry || len(r.Deficits[0].Cells) != 1 {
		t.Fatal(r, err)
	}
}

func TestFlooringTrafficTierRanksColonistStepsByPayback(t *testing.T) {
	p := flooringPolicy()
	terrains := flooringTerrains()
	terrains["Sand"] = FloorTerrain{PathCost: 4, Natural: true}
	v := FlooringObservation{Terrains: terrains, TrafficSamples: 1000, Floors: trafficFloors()}
	v.Traffic = []TrafficCell{
		{Cell: domain.Cell{X: 1, Z: 1}, Layer: TrafficColonist, Samples: 60, Terrain: "Soil", Home: true},
		{Cell: domain.Cell{X: 2, Z: 1}, Layer: TrafficColonist, Samples: 40, Terrain: "Sand", Home: true},
		{Cell: domain.Cell{X: 3, Z: 1}, Layer: TrafficVisitor, Samples: 900, Terrain: "Sand", Home: true},
	}
	r, err := ReviewFlooring(domain.Known(v), domain.Unknown[RoomObservation](), nil, p)
	// 40 steps at 4 path cost repay a wood floor sooner than 60 at 2;
	// visitor steps never pave.
	if err != nil || len(r.Deficits) != 1 || len(r.Deficits[0].Cells) != 2 || r.Deficits[0].Cells[0] != (domain.Cell{X: 2, Z: 1}) {
		t.Fatal(r, err)
	}
}
