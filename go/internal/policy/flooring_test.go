package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func flooringTerrains() map[string]FloorTerrain {
	return map[string]FloorTerrain{
		"Soil":           {Cleanliness: -1, Beauty: -3, PathCost: 2, Natural: true},
		"WoodPlankFloor": {Flammability: 0.22},
		"Concrete":       {Beauty: -1},
		"SterileTile":    {Cleanliness: 0.6},
	}
}

func flooringRoom(id string, role RoomRole, terrain string, x0, z0 int32) FloorRoom {
	room := FloorRoom{ID: id, Role: domain.Known(role)}
	for z := z0; z < z0+2; z++ {
		for x := x0; x < x0+3; x++ {
			room.Cells = append(room.Cells, FloorCell{Cell: domain.Cell{X: x, Z: z}, Terrain: terrain})
		}
	}
	return room
}

func flooringCensus() FlooringObservation {
	return FlooringObservation{
		Rooms: []FloorRoom{
			flooringRoom("bed", RoomRoleBedroom, "Soil", 20, 20),
			flooringRoom("kitchen", RoomRoleKitchen, "Soil", 10, 10),
			flooringRoom("store", RoomRoleStoreroom, "Soil", 30, 30),
			flooringRoom("done", RoomRoleKitchen, "WoodPlankFloor", 40, 40),
		},
		Terrains: flooringTerrains(),
	}
}

// flooringPolicy narrows the default floor list to the definitions the
// census fixture reports: a floor the census omits is unknown, not absent.
func flooringPolicy() FlooringPolicy {
	p := DefaultFlooringPolicy()
	p.Floors = []string{"SterileTile", "Concrete", "WoodPlankFloor"}
	return p
}

func flooringDefinitions() FlooringFacts {
	amounts := func(r Resource, n int64) domain.Fact[[]Amount] {
		return domain.Known([]Amount{{Resource: r, Count: n}})
	}
	return FlooringFacts{
		Definitions: map[string]FloorDefinition{
			"WoodPlankFloor": {Available: domain.Known(true), Terrain: domain.Known(true), Cleanliness: domain.Known(0.0), Beauty: domain.Known(0.0), Flammability: domain.Known(0.22), PathCost: domain.Known[int32](0), Costs: amounts("WoodLog", 3)},
			"Concrete":       {Available: domain.Known(false), Terrain: domain.Known(true), Cleanliness: domain.Known(0.0), Beauty: domain.Known(-1.0), Flammability: domain.Known(0.0), PathCost: domain.Known[int32](0), Costs: amounts("Steel", 1)},
			"SterileTile":    {Available: domain.Known(true), Terrain: domain.Known(true), Cleanliness: domain.Known(0.6), Beauty: domain.Known(0.0), Flammability: domain.Known(0.0), PathCost: domain.Known[int32](0), Costs: amounts("Steel", 4)},
		},
		Stock: domain.Known(map[Resource]int64{"WoodLog": 100, "Steel": 0}),
	}
}

func TestReviewFlooringTiersRoomsByRole(t *testing.T) {
	p := DefaultFlooringPolicy()
	r, err := ReviewFlooring(domain.Known(flooringCensus()), domain.Unknown[RoomObservation](), nil, p)
	if err != nil || !r.Known || !r.Active || len(r.Deficits) != 2 {
		t.Fatal(r, err)
	}
	// Clean workspaces come first, then living rooms; storerooms and floored
	// rooms have no deficit.
	if r.Deficits[0].Room != "kitchen" || r.Deficits[0].Tier != FloorTierClean || len(r.Deficits[0].Cells) != 6 || r.Deficits[0].Cells[0] != (domain.Cell{X: 10, Z: 10}) {
		t.Fatal(r.Deficits[0])
	}
	if r.Deficits[1].Room != "bed" || r.Deficits[1].Tier != FloorTierLiving || len(r.Deficits[1].Cells) != 6 {
		t.Fatal(r.Deficits[1])
	}
	if len(r.Latched) != 2 || r.Latched[0] != "10,10" || r.Latched[1] != "20,20" {
		t.Fatal(r.Latched)
	}
	// An unknown census keeps the previous latch.
	r, err = ReviewFlooring(domain.Unknown[FlooringObservation](), domain.Unknown[RoomObservation](), r.Latched, p)
	if err != nil || r.Known || !r.Active || len(r.Latched) != 2 || len(r.Deficits) != 0 {
		t.Fatal(r, err)
	}
	// A measured census with every cell laid releases the latch.
	laid := flooringCensus()
	for i := range laid.Rooms {
		for j := range laid.Rooms[i].Cells {
			laid.Rooms[i].Cells[j].Terrain = "WoodPlankFloor"
		}
	}
	r, err = ReviewFlooring(domain.Known(laid), domain.Unknown[RoomObservation](), r.Latched, p)
	if err != nil || !r.Known || r.Active || len(r.Latched) != 0 {
		t.Fatal(r, err)
	}
}

func TestReviewFlooringUsesRoomCensusContents(t *testing.T) {
	p := DefaultFlooringPolicy()
	census := FlooringObservation{Rooms: []FloorRoom{
		flooringRoom("stove", RoomRoleRoom, "Soil", 10, 10),
		flooringRoom("barn", RoomRoleBedroom, "Soil", 20, 20),
	}, Terrains: flooringTerrains()}
	rooms := domain.Known(RoomObservation{Rooms: []Room{
		{ID: "stove", Role: domain.Known(RoomRoleRoom), Enclosed: domain.Known(true), Contents: domain.Known([]Amount{{Resource: "FueledStove", Count: 1}})},
		{ID: "barn", Role: domain.Known(RoomRoleBarn), Enclosed: domain.Known(true)},
	}})
	r, err := ReviewFlooring(domain.Known(census), rooms, nil, p)
	if err != nil || len(r.Deficits) != 1 || r.Deficits[0].Room != "stove" || r.Deficits[0].Tier != FloorTierClean {
		t.Fatal(r, err)
	}
	// A plain room without a cooking bench has no requirement at all.
	r, err = ReviewFlooring(domain.Known(census), domain.Unknown[RoomObservation](), nil, p)
	if err != nil || len(r.Deficits) != 1 || r.Deficits[0].Room != "barn" || r.Deficits[0].Tier != FloorTierLiving {
		t.Fatal(r, err)
	}
}

func TestReviewFlooringConcreteIsCleanButNotBeautiful(t *testing.T) {
	p := DefaultFlooringPolicy()
	census := FlooringObservation{Rooms: []FloorRoom{
		flooringRoom("kitchen", RoomRoleKitchen, "Concrete", 10, 10),
		flooringRoom("bed", RoomRoleBedroom, "Concrete", 20, 20),
	}, Terrains: flooringTerrains()}
	r, err := ReviewFlooring(domain.Known(census), domain.Unknown[RoomObservation](), nil, p)
	if err != nil || r.Active {
		t.Fatal("concrete is a laid, clean floor for both tiers", r, err)
	}
}

func TestReviewFlooringCountsOrderedCellsAsPending(t *testing.T) {
	p := flooringPolicy()
	census := FlooringObservation{Rooms: []FloorRoom{flooringRoom("kitchen", RoomRoleKitchen, "Soil", 10, 10)}, Terrains: flooringTerrains()}
	for i := range census.Rooms[0].Cells {
		census.Rooms[0].Cells[i].Pending = "WoodPlankFloor"
	}
	r, err := ReviewFlooring(domain.Known(census), domain.Unknown[RoomObservation](), nil, p)
	if err != nil || !r.Active || len(r.Deficits) != 1 || r.Deficits[0].Pending != 6 || len(r.Deficits[0].Cells) != 0 {
		t.Fatal(r, err)
	}
	proposal, err := SelectFlooringMethod(r, flooringDefinitions(), p)
	if err != nil || proposal.Method != FlooringPending {
		t.Fatal(proposal, err)
	}
}

func TestReviewFlooringRejectsInvalidCensus(t *testing.T) {
	p := DefaultFlooringPolicy()
	census := flooringCensus()
	census.Rooms[0].Cells[0].Terrain = "Lava"
	if _, err := ReviewFlooring(domain.Known(census), domain.Unknown[RoomObservation](), nil, p); err == nil {
		t.Fatal("accepted a cell with an unlisted terrain")
	}
	census = flooringCensus()
	census.Rooms[1].Cells[0].Cell = census.Rooms[0].Cells[0].Cell
	if _, err := ReviewFlooring(domain.Known(census), domain.Unknown[RoomObservation](), nil, p); err == nil {
		t.Fatal("accepted overlapping rooms")
	}
	if _, err := ReviewFlooring(domain.Known(flooringCensus()), domain.Unknown[RoomObservation](), nil, FlooringPolicy{}); err == nil {
		t.Fatal("accepted an invalid policy")
	}
}

func TestSelectFlooringPrefersTheTierScoreAmongAffordableFloors(t *testing.T) {
	p := flooringPolicy()
	review, _ := ReviewFlooring(domain.Known(flooringCensus()), domain.Unknown[RoomObservation](), nil, p)
	facts := flooringDefinitions()
	proposal, err := SelectFlooringMethod(review, facts, p)
	// Sterile tile scores higher for a kitchen but there is no steel; wood
	// pays for every cell.
	if err != nil || proposal.Method != FlooringBuild || proposal.Definition != "WoodPlankFloor" || proposal.Room != "kitchen" || proposal.Tier != FloorTierClean || len(proposal.Cells) != 6 || proposal.Key == "" {
		t.Fatal(proposal, err)
	}
	again, _ := SelectFlooringMethod(review, facts, p)
	if again.Key != proposal.Key {
		t.Fatal("method key is not stable", proposal.Key, again.Key)
	}
	facts.Stock = domain.Known(map[Resource]int64{"WoodLog": 100, "Steel": 100})
	proposal, err = SelectFlooringMethod(review, facts, p)
	if err != nil || proposal.Definition != "SterileTile" {
		t.Fatal(proposal, err)
	}
	// A floor paying for the whole batch beats a better one paying for part
	// of it; partial stock lays what it pays for and the key follows the batch.
	facts.Stock = domain.Known(map[Resource]int64{"WoodLog": 100, "Steel": 8})
	partial, err := SelectFlooringMethod(review, facts, p)
	if err != nil || partial.Definition != "WoodPlankFloor" || len(partial.Cells) != 6 {
		t.Fatal(partial, err)
	}
	facts.Stock = domain.Known(map[Resource]int64{"WoodLog": 9, "Steel": 8})
	partial, err = SelectFlooringMethod(review, facts, p)
	if err != nil || partial.Definition != "WoodPlankFloor" || len(partial.Cells) != 3 || partial.Key == proposal.Key {
		t.Fatal(partial, err)
	}
	// Unknown stock leaves affordability to admission.
	facts.Stock = domain.Unknown[map[Resource]int64]()
	proposal, err = SelectFlooringMethod(review, facts, p)
	if err != nil || proposal.Definition != "SterileTile" || len(proposal.Cells) != 6 {
		t.Fatal(proposal, err)
	}
}

func TestSelectFlooringDefersWithoutMaterialsOrResearch(t *testing.T) {
	p := flooringPolicy()
	review, _ := ReviewFlooring(domain.Known(flooringCensus()), domain.Unknown[RoomObservation](), nil, p)
	facts := flooringDefinitions()
	facts.Stock = domain.Known(map[Resource]int64{})
	proposal, err := SelectFlooringMethod(review, facts, p)
	if err != nil || proposal.Method != FlooringMaterialsNeeded {
		t.Fatal(proposal, err)
	}
	for name, d := range facts.Definitions {
		d.Available = domain.Known(false)
		facts.Definitions[name] = d
	}
	proposal, err = SelectFlooringMethod(review, facts, p)
	if err != nil || proposal.Method != FlooringResearchNeeded {
		t.Fatal(proposal, err)
	}
	// A definition missing from the census leaves the choice unknown.
	proposal, err = SelectFlooringMethod(review, FlooringFacts{Definitions: map[string]FloorDefinition{}}, p)
	if err != nil || proposal.Method != FlooringUnknown {
		t.Fatal(proposal, err)
	}
	if proposal, err := SelectFlooringMethod(FlooringReview{}, facts, p); err != nil || proposal.Method != FlooringNoMethod {
		t.Fatal(proposal, err)
	}
	if proposal, err := SelectFlooringMethod(FlooringReview{Active: true}, facts, p); err != nil || proposal.Method != FlooringUnknown {
		t.Fatal(proposal, err)
	}
}

func TestSelectFlooringBoundsTheBatch(t *testing.T) {
	p := flooringPolicy()
	p.MaxCellsPerPlan = 4
	review, _ := ReviewFlooring(domain.Known(flooringCensus()), domain.Unknown[RoomObservation](), nil, p)
	proposal, err := SelectFlooringMethod(review, flooringDefinitions(), p)
	if err != nil || len(proposal.Cells) != 4 || proposal.Cells[0] != (domain.Cell{X: 10, Z: 10}) {
		t.Fatal(proposal, err)
	}
}

func TestSelectFlooringRefusesUglyFloorsForLivingRooms(t *testing.T) {
	p := flooringPolicy()
	p.Floors = []string{"Concrete"}
	census := FlooringObservation{Rooms: []FloorRoom{flooringRoom("bed", RoomRoleBedroom, "Soil", 20, 20)}, Terrains: flooringTerrains()}
	review, _ := ReviewFlooring(domain.Known(census), domain.Unknown[RoomObservation](), nil, p)
	facts := flooringDefinitions()
	concrete := facts.Definitions["Concrete"]
	concrete.Available = domain.Known(true)
	facts.Definitions = map[string]FloorDefinition{"Concrete": concrete}
	facts.Stock = domain.Known(map[Resource]int64{"Steel": 100})
	proposal, err := SelectFlooringMethod(review, facts, p)
	if err != nil || proposal.Method != FlooringResearchNeeded {
		t.Fatal("concrete is not a living-room floor", proposal, err)
	}
}

// trafficFloors is the policy floors' planning rows with their native
// WorkToBuild, as the traffic tier prices them (#950).
func trafficFloors() map[string]FloorDefinition {
	floors := flooringDefinitions().Definitions
	for name, work := range map[string]float64{"WoodPlankFloor": 85, "Concrete": 50, "SterileTile": 800} {
		d := floors[name]
		d.WorkToBuild = domain.Known(work)
		floors[name] = d
	}
	return floors
}

// The traffic tier keeps a cell only while the priced floor repays its
// work and material ticks within PaybackDays (#950). A wood floor costs
// 85 work + 3 logs x 30 = 175 ticks; a colonist cell of s samples is
// stepped s*ln2/2 times a day.
func TestFlooringTrafficTierPaysBack(t *testing.T) {
	terrains := flooringTerrains()
	terrains["Sand"] = FloorTerrain{PathCost: 8, Natural: true}
	cell := func(x int32, samples uint32, terrain string) TrafficCell {
		return TrafficCell{Cell: domain.Cell{X: x, Z: 1}, Layer: TrafficColonist, Samples: samples, Terrain: terrain, Home: true}
	}
	for _, tc := range []struct {
		name    string
		traffic []TrafficCell
		floors  func(map[string]FloorDefinition)
		want    []int32
		active  bool
		unknown bool
	}{
		// 12 samples on sand: 4.2 steps x 8 ticks = 33 ticks a day, 5.3 days.
		{name: "busy sand pays", traffic: []TrafficCell{cell(1, 12, "Sand")}, want: []int32{1}, active: true},
		// 12 samples on soil: 8.3 ticks a day, 21 days.
		{name: "lightly walked soil does not", traffic: []TrafficCell{cell(1, 12, "Soil")}},
		// Sand 12 (5.3 d), soil 60 (4.2 d), soil 30 (8.4 d), soil 12 (21 d),
		// sand 40 (1.6 d): cheapest payback first, the non-payer cut.
		{name: "ordered by payback, stops at the first non-payer",
			traffic: []TrafficCell{cell(1, 12, "Sand"), cell(2, 60, "Soil"), cell(3, 30, "Soil"), cell(4, 12, "Soil"), cell(5, 40, "Sand")},
			want:    []int32{5, 2, 1, 3}, active: true},
		{name: "unknown work is unknown", traffic: []TrafficCell{cell(1, 40, "Sand")}, floors: func(f map[string]FloorDefinition) {
			for name, d := range f {
				d.WorkToBuild = domain.Unknown[float64]()
				f[name] = d
			}
		}, unknown: true},
		{name: "no floor rows is unknown", traffic: []TrafficCell{cell(1, 40, "Sand")}, floors: func(f map[string]FloorDefinition) { clear(f) }, unknown: true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			p := flooringPolicy()
			v := FlooringObservation{Terrains: terrains, TrafficSamples: 1000, Traffic: tc.traffic, Floors: trafficFloors()}
			if tc.floors != nil {
				tc.floors(v.Floors)
			}
			r, err := ReviewFlooring(domain.Known(v), domain.Unknown[RoomObservation](), nil, p)
			if err != nil || r.Active != tc.active {
				t.Fatal(r, err)
			}
			if tc.unknown {
				// A traffic latch held before survives an unpriced review and
				// the selector waits on it.
				r, err = ReviewFlooring(domain.Known(v), domain.Unknown[RoomObservation](), []string{trafficKey}, p)
				if err != nil || !r.Active || len(r.Deficits) != 1 || !r.Deficits[0].Unknown || len(r.Latched) != 1 {
					t.Fatal(r, err)
				}
				if proposal, err := SelectFlooringMethod(r, flooringDefinitions(), p); err != nil || proposal.Method != FlooringUnknown {
					t.Fatal(proposal, err)
				}
				return
			}
			if !tc.active {
				return
			}
			d := r.Deficits[0]
			if d.Floor != "WoodPlankFloor" || len(d.Cells) != len(tc.want) {
				t.Fatal(d)
			}
			for i, x := range tc.want {
				if d.Cells[i].X != x {
					t.Fatal(d.Cells)
				}
			}
			proposal, err := SelectFlooringMethod(r, flooringDefinitions(), p)
			if err != nil || proposal.Method != FlooringBuild || proposal.Definition != d.Floor {
				t.Fatal(proposal, err)
			}
		})
	}
}

// A zero PaybackDays disables the traffic tier, and the tier style is the
// floor priced when it can be laid.
func TestFlooringTrafficTierStyleAndDisable(t *testing.T) {
	terrains := flooringTerrains()
	terrains["Sand"] = FloorTerrain{PathCost: 8, Natural: true}
	v := FlooringObservation{Terrains: terrains, TrafficSamples: 1000, Floors: trafficFloors(), Stock: domain.Known(map[Resource]int64{"WoodLog": 100, "Steel": 100}), TrafficStyle: "SterileTile"}
	v.Traffic = []TrafficCell{{Cell: domain.Cell{X: 1, Z: 1}, Layer: TrafficColonist, Samples: 200, Terrain: "Sand", Home: true}}
	p := flooringPolicy()
	r, err := ReviewFlooring(domain.Known(v), domain.Unknown[RoomObservation](), nil, p)
	if err != nil || len(r.Deficits) != 1 || r.Deficits[0].Floor != "SterileTile" {
		t.Fatal(r, err)
	}
	p.PaybackDays = 0
	if r, err = ReviewFlooring(domain.Known(v), domain.Unknown[RoomObservation](), nil, p); err != nil || r.Active {
		t.Fatal(r, err)
	}
}
