package policy

import (
	"errors"
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func region(w, h int32) Region { return RectRegion(Rectangle{X: 0, Z: 0, Width: w, Height: h}) }

func built(room int32, role RoomRole, w, h int32) BuiltRegion {
	return BuiltRegion{Room: room, Role: role, Region: region(w, h)}
}

func group(id int, role RoomRole, area int) RoomGroup {
	return RoomGroup{ID: id, Role: role, Area: area}
}

func TestMatchGroupRoomsPairsLargestWithLargest(t *testing.T) {
	groups := []RoomGroup{group(0, RoomRoleKitchen, 20), group(1, RoomRoleStoreroom, 40), group(2, RoomRoleStoreroom, 60)}
	rooms := []BuiltRegion{built(5, RoomRoleStoreroom, 5, 5), built(6, RoomRoleStoreroom, 6, 8), built(7, RoomRoleHospital, 3, 3)}
	got := MatchGroupRooms(groups, rooms)
	want := []GroupRooms{
		{Group: 0},
		{Group: 1, Rooms: []int32{5}, Capacity: 25},
		{Group: 2, Rooms: []int32{6}, Capacity: 48},
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("match = %+v, want %+v", got, want)
	}
	if GroupOutgrown(groups[0], got[0]) {
		t.Error("a group with no built room has nothing to outgrow")
	}
	if !GroupOutgrown(groups[1], got[1]) || !GroupOutgrown(groups[2], got[2]) {
		t.Error("both storerooms are smaller than their groups")
	}
}

func TestMatchGroupRoomsBreaksTiesByID(t *testing.T) {
	groups := []RoomGroup{group(3, RoomRoleBedroom, 12), group(1, RoomRoleBedroom, 12)}
	rooms := []BuiltRegion{built(9, RoomRoleBedroom, 3, 4), built(4, RoomRoleBedroom, 3, 4)}
	got := MatchGroupRooms(groups, rooms)
	// Group order follows the input; equal groups and rooms pair by lower id.
	if !reflect.DeepEqual(got[0].Rooms, []int32{9}) || !reflect.DeepEqual(got[1].Rooms, []int32{4}) {
		t.Fatalf("match = %+v, want group 1 -> room 4 and group 3 -> room 9", got)
	}
}

func TestMatchGroupRoomsGivesSurplusRoomsToTheGroupMostShort(t *testing.T) {
	groups := []RoomGroup{group(0, RoomRoleStoreroom, 30), group(1, RoomRoleStoreroom, 100)}
	rooms := []BuiltRegion{built(1, RoomRoleStoreroom, 10, 10), built(2, RoomRoleStoreroom, 5, 5), built(3, RoomRoleStoreroom, 4, 4), built(4, RoomRoleStoreroom, 2, 2)}
	got := MatchGroupRooms(groups, rooms)
	// Group 1 (100) gets the 100-cell room, group 0 (30) the 25. Then 16 goes
	// to group 0 (30-25=5 short beats 100-100=0), and 4 to group 0 again
	// (30-41 is -11, but group 1 is 0 short: group 1 is now the more short).
	if !reflect.DeepEqual(got[0].Rooms, []int32{2, 3}) || !reflect.DeepEqual(got[1].Rooms, []int32{1, 4}) {
		t.Fatalf("match = %+v", got)
	}
	if got[0].Capacity != 41 || got[1].Capacity != 104 {
		t.Errorf("capacities %d, %d", got[0].Capacity, got[1].Capacity)
	}
	for _, g := range groups {
		if GroupOutgrown(g, got[g.ID]) {
			t.Errorf("group %d is housed by %d cells", g.ID, got[g.ID].Capacity)
		}
	}
}

func TestMatchGroupRoomsOutgrownIsUnderSized(t *testing.T) {
	g := group(0, RoomRoleKitchen, 36)
	for _, c := range []struct {
		w, h int32
		want bool
	}{{5, 5, true}, {6, 6, false}, {7, 6, false}} {
		got := MatchGroupRooms([]RoomGroup{g}, []BuiltRegion{built(1, RoomRoleKitchen, c.w, c.h)})
		if GroupOutgrown(g, got[0]) != c.want {
			t.Errorf("%dx%d: outgrown = %v, want %v", c.w, c.h, !c.want, c.want)
		}
	}
	if none := MatchGroupRooms([]RoomGroup{g}, nil); GroupOutgrown(g, none[0]) || len(none[0].Rooms) != 0 {
		t.Errorf("none built: %+v", none)
	}
}

func TestBuiltRegionsAreStandingNativeRoles(t *testing.T) {
	plan := LayoutPlan{Rooms: []PlannedRoom{
		{ID: 1, Role: PlannedKitchen, Region: region(4, 4)},
		{ID: 2, Role: PlannedKitchen, Region: region(4, 4)},
		{ID: 3, Role: PlannedBarn, Region: region(4, 4), Outdoor: true},
		{ID: 4, Role: PlannedWasteYard, Region: region(4, 4)},
		{ID: 5, Role: PlannedArmory, Region: region(4, 4)},
	}}
	got := BuiltRegions(plan, map[int32]bool{1: true, 3: true, 4: true, 5: true})
	if len(got) != 2 || got[0].Room != 1 || got[0].Role != RoomRoleKitchen || got[1].Room != 5 || got[1].Role != RoomRoleStoreroom {
		t.Fatalf("built regions = %+v, want kitchen 1 and storeroom 5 only", got)
	}
}

func stationFactsFixture() StationFacts {
	cat := clusterCatalog()
	bed, bench, shelf := cat.Stations["Bed"], cat.Stations["Bench"], cat.Stations["Shelf"]
	bed.Capacity = StationCapacity{Kind: CapacitySleepers, Slots: 1}
	bench.Capacity = StationCapacity{Kind: CapacityWorkers, Slots: 1, Recipes: []RecipeWorkAmount{{"MakeThing", 30000}}}
	shelf.Capacity = StationCapacity{Kind: CapacityItems, Slots: 4}
	cat.Stations["Bed"], cat.Stations["Bench"], cat.Stations["Shelf"] = bed, bench, shelf
	survey := plainScoreSurvey()
	plan := LayoutPlan{Zones: Zone(survey), Rooms: []PlannedRoom{{ID: 1, Role: PlannedWorkshop, Region: region(3, 3)}}}
	return StationFacts{
		Catalog:    cat,
		Furniture:  RoomFurniture{Beds: []FurnitureBed{{Def: "Bed", Slots: 1}}},
		Dining:     DiningFurniture{Table: InteriorPieceDef{Def: "Table", Size: domain.Cell{X: 1, Z: 2}}},
		Recipes:    map[string]RecipeFlow{"MakeThing": {Ingredients: [][]Amount{{{Resource: "Steel", Count: 2}}}, Products: []Amount{{Resource: "Thing", Count: 1}}}},
		StackLimit: map[Resource]int64{"Steel": 75}, Perishable: map[Resource]bool{},
		Colonists: domain.Known(int64(4)), Patients: domain.Known([]CarePawn{}), Prisoners: domain.Known(0), WorkPawns: domain.Known([]WorkPawn{}),
		Food:         domain.Known(FoodSupply{}),
		Orders:       domain.Known([]OrderSpec{{Recipe: "MakeThing", BenchKind: "Bench", Mode: domain.StockTarget, Target: 3}}),
		Stock:        domain.Known(map[Resource]int64{"Steel": 90}),
		RoofDistance: testRoof, Plan: plan, Built: map[int32]bool{1: true}, Survey: plainScoreSurvey(),
	}
}

func TestAdviseStationsRunsTheChainAndChangesNothing(t *testing.T) {
	f := stationFactsFixture()
	plan, survey := f.Plan, f.Survey
	planCopy := LayoutPlan{Zones: slices.Clone(plan.Zones), Rooms: slices.Clone(plan.Rooms)}
	surveyCells := slices.Clone(survey.Cells)
	advice, err := AdviseStations(f)
	if err != nil {
		t.Fatal(err)
	}
	if len(advice.Graph.Nodes) == 0 || len(advice.Clustering.Groups) == 0 || len(advice.Embedding.Blocks) == 0 || len(advice.Groups) != len(advice.Clustering.Groups) {
		t.Fatalf("chain did not run: %d nodes, %d groups, %d blocks, %d advised", len(advice.Graph.Nodes), len(advice.Clustering.Groups), len(advice.Embedding.Blocks), len(advice.Groups))
	}
	matched := 0
	for _, g := range advice.Groups {
		if len(g.Rooms) > 0 {
			matched++
			if g.Group.Role != RoomRoleWorkshop || !reflect.DeepEqual(g.Rooms, []int32{1}) || g.Capacity != 9 {
				t.Errorf("matched group %+v", g)
			}
		}
	}
	if matched != 1 {
		t.Errorf("the one standing workshop matched %d groups", matched)
	}
	if !reflect.DeepEqual(f.Plan, planCopy) || !reflect.DeepEqual(f.Survey.Cells, surveyCells) {
		t.Error("advice changed the plan or the survey")
	}
	again, err := AdviseStations(f)
	if err != nil || !reflect.DeepEqual(advice, again) {
		t.Errorf("advice is not deterministic (err %v)", err)
	}
}

func TestAdviseStationsTilesOnlyInTileStations(t *testing.T) {
	f := stationFactsFixture()
	plain, err := AdviseStations(f)
	if err != nil || plain.Tiled != nil {
		t.Fatalf("advice without TileStations: tiled %v, err %v", plain.Tiled, err)
	}
	advice, err := TileStations(f)
	if err != nil {
		t.Fatal(err)
	}
	if advice.Tiled == nil || len(advice.Tiled.Plan.Rooms) != len(advice.Embedding.Blocks) {
		t.Fatalf("TileStations: %+v for %d blocks", advice.Tiled, len(advice.Embedding.Blocks))
	}
}

func TestAdviseStationsNamesAnUnknownFact(t *testing.T) {
	for name, edit := range map[string]func(*StationFacts){
		"colonists": func(f *StationFacts) { f.Colonists = domain.Unknown[int64]() },
		"patients":  func(f *StationFacts) { f.Patients = domain.Unknown[[]CarePawn]() },
		"prisoners": func(f *StationFacts) { f.Prisoners = domain.Unknown[int]() },
		"pawn":      func(f *StationFacts) { f.WorkPawns = domain.Unknown[[]WorkPawn]() },
		"food":      func(f *StationFacts) { f.Food = domain.Unknown[FoodSupply]() },
		"orders":    func(f *StationFacts) { f.Orders = domain.Unknown[[]OrderSpec]() },
		"demand":    func(f *StationFacts) { f.Stock = domain.Unknown[map[Resource]int64]() },
		"built":     func(f *StationFacts) { f.Built = nil },
		"dining":    func(f *StationFacts) { f.Dining = DiningFurniture{} },
		"rate": func(f *StationFacts) {
			f.Food = domain.Known(FoodSupply{Consumers: []FoodConsumer{{ID: "a", NutritionPerDay: domain.Unknown[float64]()}}})
		},
	} {
		f := stationFactsFixture()
		edit(&f)
		if _, err := AdviseStations(f); !errors.Is(err, ErrStationFacts) {
			t.Errorf("%s: err = %v, want ErrStationFacts", name, err)
		}
	}
}

func TestStationPopulationOfSplitsBabiesAndPatients(t *testing.T) {
	f := stationFactsFixture()
	baby := WorkPawn{Biotech: domain.Known(PawnBiotech{DevelopmentalStage: domain.Known("Baby")})}
	adult := WorkPawn{Biotech: domain.Known(PawnBiotech{DevelopmentalStage: domain.Known("Adult")})}
	f.WorkPawns = domain.Known([]WorkPawn{baby, adult})
	f.Prisoners = domain.Known(2)
	f.Animals = 3
	f.Patients = domain.Known([]CarePawn{
		{ID: "hurt", Dead: domain.Known(false), NeedsRest: domain.Known(true)},
		{ID: "well", Dead: domain.Known(false), NeedsRest: domain.Known(false)},
		{ID: "dead", Dead: domain.Known(true), NeedsRest: domain.Known(true)},
	})
	got, err := StationPopulationOf(f)
	if err != nil {
		t.Fatal(err)
	}
	if want := (StationPopulation{Colonists: 3, Injured: 1, Prisoners: 2, Babies: 1, Animals: 3}); got != want {
		t.Errorf("population = %+v, want %+v", got, want)
	}
}
