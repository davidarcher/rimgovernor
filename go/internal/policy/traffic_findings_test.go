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

func TestFlooringBarnTierLaysStrawMatting(t *testing.T) {
	p := flooringPolicy()
	terrains := flooringTerrains()
	terrains["StrawMatting"] = FloorTerrain{Cleanliness: -0.1, Beauty: -1, PathCost: 1}
	v := FlooringObservation{Rooms: []FloorRoom{flooringRoom("barn", RoomRoleBarn, "Soil", 10, 10)}, Terrains: terrains}
	r, err := ReviewFlooring(domain.Known(v), domain.Unknown[RoomObservation](), nil, p)
	if err != nil || len(r.Deficits) != 1 || r.Deficits[0].Tier != FloorTierBarn || len(r.Deficits[0].Cells) != 6 {
		t.Fatal(r, err)
	}
	facts := styledFacts(StrawMatting)
	facts.Definitions[StrawMatting] = FloorDefinition{Available: domain.Known(true), Terrain: domain.Known(true), Cleanliness: domain.Known(-0.1), Beauty: domain.Known(-1.0), Flammability: domain.Known(1.5), PathCost: domain.Known[int32](1), Costs: domain.Known([]Amount{{Resource: "Hay", Count: 2}})}
	facts.Stock = domain.Known(map[Resource]int64{"WoodLog": 100, "Hay": 40})
	proposal, err := SelectFlooringMethod(r, facts, p)
	if err != nil || proposal.Method != FlooringBuild || proposal.Tier != FloorTierBarn || proposal.Definition != StrawMatting || len(proposal.Cells) != 6 {
		t.Fatal(proposal, err)
	}
	// Once matted, despite the mat's negative beauty and cleanliness, the barn
	// has no deficit.
	for i := range v.Rooms[0].Cells {
		v.Rooms[0].Cells[i].Terrain = StrawMatting
	}
	if r, err = ReviewFlooring(domain.Known(v), domain.Unknown[RoomObservation](), nil, p); err != nil || len(r.Deficits) != 0 {
		t.Fatal(r, err)
	}
}

func TestFlooringTrafficTierRanksColonistStepsByPayback(t *testing.T) {
	p := flooringPolicy()
	terrains := flooringTerrains()
	terrains["Sand"] = FloorTerrain{PathCost: 4, Natural: true}
	v := FlooringObservation{Terrains: terrains, TrafficSamples: 1000, Floors: trafficFloors(), TrafficStyle: "WoodPlankFloor"}
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

// With no tier style the traffic tier lays nothing, however cheap or steel-free
// the policy floors would be.
func TestFlooringTrafficTierNeverSubstitutesAFloor(t *testing.T) {
	p := flooringPolicy()
	terrains := flooringTerrains()
	terrains["Sand"] = FloorTerrain{PathCost: 8, Natural: true}
	v := FlooringObservation{Terrains: terrains, TrafficSamples: 1000, Floors: trafficFloors()}
	v.Traffic = []TrafficCell{{Cell: domain.Cell{X: 1, Z: 1}, Layer: TrafficColonist, Samples: 400, Terrain: "Sand", Home: true}}
	if r, err := ReviewFlooring(domain.Known(v), domain.Unknown[RoomObservation](), nil, p); err != nil || r.Active {
		t.Fatal("paved without a style", r, err)
	}
	// A styled floor the stock cannot pay for waits instead of falling to wood.
	v.TrafficStyle = "SterileTile"
	v.Stock = domain.Known(map[Resource]int64{"WoodLog": 100})
	if r, err := ReviewFlooring(domain.Known(v), domain.Unknown[RoomObservation](), nil, p); err != nil || r.Active {
		t.Fatal("substituted wood for the styled floor", r, err)
	}
}
