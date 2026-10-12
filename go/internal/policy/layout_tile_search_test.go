package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// searchTiler is a tiler holding what the interior search reads, over the
// objective test's station graph and the test catalog's shapes.
func searchTiler(t testing.TB) *tiler {
	stations := map[string]Station{}
	for def, shape := range testShapes.Defs {
		stations[def] = Station{Def: def, Shape: shape}
	}
	return &tiler{in: TileInput{
		Catalog: StationCatalog{Stations: stations}, Graph: objectiveGraph(t), Furniture: testFurniture, Dining: testDining,
		Embedding: Embedding{Seed: domain.Cell{X: 4, Z: 9}},
	}}
}

func wantOf(slot, def string, count int) RegionWant {
	s := testShapes.Defs[def]
	return RegionWant{Slot: slot, Def: def, Size: s.Size, Interaction: s.Interaction, Count: count, Min: count}
}

// interiorCases are two irregular regions for each major role, with the stations
// the role asks for and a door beside the region's top row.
func interiorCases() map[string]struct {
	role   RoomRole
	region Region
	door   domain.Cell
	wants  []RegionWant
} {
	l := regionOfRows("######", "######", "######", "####..", "####..")
	notch := regionOfRows("######", "######", "##..##", "######", "######")
	type c = struct {
		role   RoomRole
		region Region
		door   domain.Cell
		wants  []RegionWant
	}
	door := domain.Cell{X: 1, Z: -1}
	shelves := []RegionWant{wantOf("ShelfSmall", "ShelfSmall", 6)}
	bench := []RegionWant{wantOf("bench", testWorkshop, 2)}
	kitchen := []RegionWant{wantOf("stove", testStove, 1), wantOf("table", testDining.Table.Def, 1)}
	beds := []RegionWant{wantOf("Bed", "Bed", 3)}
	return map[string]c{
		"storage/L": {RoomRoleStoreroom, l, door, shelves}, "storage/notch": {RoomRoleStoreroom, notch, door, shelves},
		"workshop/L": {RoomRoleWorkshop, l, door, bench}, "workshop/notch": {RoomRoleWorkshop, notch, door, bench},
		"kitchen/L": {RoomRoleKitchen, l, door, kitchen}, "kitchen/notch": {RoomRoleKitchen, notch, door, kitchen},
		"bedroom/L": {RoomRoleBedroom, l, door, beds}, "bedroom/notch": {RoomRoleBedroom, notch, door, beds},
	}
}

// The tiler's search on irregular rooms of every major role: the layout is
// deterministic for equal inputs, feasible, holds every station asked for, and
// scores no worse than the greedy layout it started from on any gated term.
func TestTilerInteriorSearchIrregularRooms(t *testing.T) {
	tl := searchTiler(t)
	for name, c := range interiorCases() {
		t.Run(name, func(t *testing.T) {
			doors := []domain.Cell{c.door}
			req := RegionPlacement{Region: c.region, Doors: doors, Wants: c.wants}
			req.Search = tl.interiorSearch(7, c.role, doors)
			res, err := SearchInterior(req, *req.Search)
			if err != nil {
				t.Fatal(err)
			}
			again, _ := SearchInterior(req, *tl.interiorSearch(7, c.role, doors))
			if !reflect.DeepEqual(res, again) {
				t.Fatal("not deterministic")
			}
			want := 0
			for _, w := range c.wants {
				want += int(w.Count)
			}
			if !res.Score.Feasible() || len(res.Stood()) != want {
				t.Fatalf("score %+v, %d of %d pieces", res.Score.Violations, len(res.Stood()), want)
			}
			if worse := res.Score.WorseTerms(res.Seed); len(worse) > 0 {
				t.Fatalf("search worse than greedy on %v: %+v vs %+v", worse, res.Score, res.Seed)
			}
			pieces, moves, err := placeAndSearch(req)
			if err != nil || !reflect.DeepEqual(pieces, res.Stood()) || len(moves) != len(res.Moves) {
				t.Fatalf("placeAndSearch differs: %v", err)
			}
		})
	}
}

// Rooms of different ids search on different seeds, equal ids on equal ones.
func TestTilerInteriorSearchSeedFollowsRoomAndSiting(t *testing.T) {
	tl := searchTiler(t)
	doors := []domain.Cell{{X: 1, Z: -1}}
	seed := func(tl *tiler, id int32) uint64 { return tl.interiorSearch(id, RoomRoleKitchen, doors).Seed }
	same := searchTiler(t)
	if seed(tl, 1) == seed(tl, 2) || seed(tl, 1) != seed(same, 1) {
		t.Error("seed does not follow the room id")
	}
	other := searchTiler(t)
	other.in.Embedding.Seed = domain.Cell{X: 5, Z: 9}
	if seed(tl, 1) == seed(other, 1) {
		t.Error("seed does not follow the siting seed")
	}
}

// The tiled fixture's rooms with the search on: tiling is deterministic, each
// room scores at least its greedy layout on every gated term, and the kept
// moves are decisions.
func TestTilerInteriorSearchFixture(t *testing.T) {
	f := colonyStationFacts(40, plainScoreSurvey())
	out, err := AdviseStations(f)
	if err != nil {
		t.Fatal(err)
	}
	in := f.tileInput(out)
	searched, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	again, err := TileEmbedding(in)
	if err != nil || !reflect.DeepEqual(searched, again) {
		t.Fatalf("tiling is not deterministic with the search on (err %v)", err)
	}
	plain := in
	plain.Graph = StationGraph{}
	greedy, err := TileEmbedding(plain)
	if err != nil {
		t.Fatal(err)
	}
	tl := &tiler{in: in}
	rooms := 0
	for _, room := range searched.Plan.Rooms {
		pieces, base := searched.Pieces[room.ID], greedy.Pieces[room.ID]
		if len(pieces) == 0 {
			continue
		}
		rooms++
		doors := append([]domain.Cell{room.Door}, searched.Plan.LinkCells(room.ID)...)
		sortCells(doors)
		doors = slices.Compact(doors)
		group := -1
		for g, id := range searched.Rooms {
			if id == room.ID {
				group = g
			}
		}
		s := tl.interiorSearch(room.ID, in.Groups[group].Role, doors)
		required := map[string]int{}
		for _, p := range base {
			required[p.Def]++
		}
		score := func(ps []InteriorPiece) InteriorScore {
			return ScoreInterior(InteriorObjective{Region: room.Region, Candidates: doors, Doors: doors, Pieces: ps, Required: required, Shapes: s.Shapes, Flows: s.Flows, Dining: s.Dining})
		}
		got, was := score(pieces), score(base)
		if !got.Feasible() && was.Feasible() || got.Feasible() && len(got.WorseTerms(was)) > 0 {
			t.Errorf("room %d (%s): searched %+v worse than greedy %+v", room.ID, room.Role, got, was)
		}
	}
	if rooms == 0 {
		t.Fatal("no room was furnished")
	}
	moves := 0
	for _, d := range searched.Decisions {
		if d.Reason == ReasonInteriorSearchMove {
			moves++
			if d.Verdict != TilePlace || d.Move == "" {
				t.Errorf("search decision %+v", d)
			}
		}
	}
	for _, d := range greedy.Decisions {
		if d.Reason == ReasonInteriorSearchMove {
			t.Errorf("greedy tiling decided a search move: %+v", d)
		}
	}
	if moves == 0 {
		t.Error("a forty-colonist base kept no search move: no decision was emitted")
	}
	t.Logf("%d furnished rooms, %d kept moves", rooms, moves)
}

// BenchmarkInteriorSearchPerRoom is the cost of searching one irregular room
// at the default budget.
func BenchmarkInteriorSearchPerRoom(b *testing.B) {
	tl := searchTiler(b)
	for name, c := range interiorCases() {
		b.Run(name, func(b *testing.B) {
			doors := []domain.Cell{c.door}
			req := RegionPlacement{Region: c.region, Doors: doors, Wants: c.wants}
			req.Search = tl.interiorSearch(7, c.role, doors)
			b.ReportAllocs()
			for range b.N {
				if _, _, err := placeAndSearch(req); err != nil {
					b.Fatal(err)
				}
			}
		})
	}
}
