package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// soilPatchSurvey is plain walkable ground whose only fertile soil is the
// patch (x 100-129, z 50-89); the rest grows nothing.
func soilPatchSurvey() MapSurvey {
	return zoningSurvey(140, func(x, z int32) SurveyCell {
		c := SurveyCell{Walkable: true}
		if x >= 100 && x < 130 && z >= 50 && z < 90 {
			c.Fertility = 1.4
		}
		return c
	})
}

func groupOfStation(t *testing.T, groups []RoomGroup, def string) int {
	t.Helper()
	for _, g := range groups {
		for _, s := range g.Stations {
			if s.Def == def {
				return g.ID
			}
		}
	}
	t.Fatalf("no group holds %s", def)
	return -1
}

func fieldEmbedInput(t *testing.T, weight float64, area int) (EmbedInput, int) {
	t.Helper()
	in := embedInput(t, soilPatchSurvey(), 3)
	kitchen := groupOfStation(t, in.Groups, "Stove")
	field := EmbedField{Node: FieldNode{ID: "field/Plant_Rice", Area: area, Mask: ZoneField, FertilityMin: 1}}
	if weight > 0 {
		field.Links = []GroupLink{{To: kitchen, Weight: weight}}
	}
	in.Fields = []EmbedField{field}
	return in, kitchen
}

func cellsOf(r Rectangle) []domain.Cell {
	var out []domain.Cell
	for z := r.Z; z < r.Z+r.Height; z++ {
		for x := r.X; x < r.X+r.Width; x++ {
			out = append(out, domain.Cell{X: x, Z: z})
		}
	}
	return out
}

// A field lands on soil, not over a room or its wall ring, and the result is
// deterministic.
func TestEmbedPlacesFieldOnSoil(t *testing.T) {
	in, _ := fieldEmbedInput(t, 20, 100)
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	checkEmbedding(t, in, emb)
	field, ok := emb.Field("field/Plant_Rice")
	if !ok || int(field.Width*field.Height) < 100 {
		t.Fatalf("field %+v placed %v, decisions %+v", field, ok, emb.Decisions)
	}
	soil := map[domain.Cell]bool{}
	for _, c := range in.Survey.Cells {
		soil[c.Cell] = c.Fertility >= 1
	}
	for _, c := range cellsOf(field) {
		if !soil[c] {
			t.Fatalf("field cell %v is not soil", c)
		}
	}
	for _, b := range emb.Blocks {
		if collides(field, b.Interior) {
			t.Errorf("field %+v collides with room %+v", field, b.Interior)
		}
	}
	var placed bool
	for _, d := range emb.Decisions {
		if d.Field == "field/Plant_Rice" {
			placed = d.Verdict == EmbedPlace && d.Reason == ReasonEmbedFieldPlaced
		}
	}
	if !placed {
		t.Errorf("no field decision in %+v", emb.Decisions)
	}
	again, err := Embed(in)
	if err != nil || !reflect.DeepEqual(emb, again) {
		t.Errorf("embedding is not deterministic (err %v)", err)
	}
}

// Field against room is one walk term: the kitchen with a heavy crop flow
// stands nearer the field than the kitchen with none.
func TestEmbedWalkTermPullsKitchenToField(t *testing.T) {
	gap := func(weight float64) int {
		in, kitchen := fieldEmbedInput(t, weight, 100)
		emb, err := Embed(in)
		if err != nil {
			t.Fatal(err)
		}
		field, ok := emb.Field("field/Plant_Rice")
		block, placed := emb.Block(kitchen)
		if !ok || !placed {
			t.Fatalf("field %v kitchen %v: %+v", ok, placed, emb.Decisions)
		}
		return fieldGap(field, block)
	}
	apart, linked := gap(0), gap(5000)
	if linked >= apart {
		t.Errorf("kitchen gap with the crop flow %d, without %d: the walk term did not move it", linked, apart)
	}
}

// A field no soil fits is reported, with the reason, and places nothing.
func TestEmbedFieldWithoutSoilIsUnplaceable(t *testing.T) {
	in, _ := fieldEmbedInput(t, 20, 5000)
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	if len(emb.Fields) != 0 || len(emb.Blocks) != len(in.Groups) {
		t.Fatalf("fields %+v blocks %d", emb.Fields, len(emb.Blocks))
	}
	var found bool
	for _, d := range emb.Unplaceable() {
		found = found || d.Field == "field/Plant_Rice" && d.Reason == ReasonEmbedFieldNoSoil
	}
	if !found {
		t.Errorf("decisions %+v", emb.Decisions)
	}
}

// A field's cell terms are per node: a 400-cell field costs what a 16-cell one
// does, where a room's cell terms scale with its area.
func TestEmbedFieldCellTermsAreNormalisedPerNode(t *testing.T) {
	in, _ := fieldEmbedInput(t, 0, 400)
	in.Fields = append(in.Fields, EmbedField{Node: FieldNode{ID: "field/small", Area: 16, Mask: ZoneField, FertilityMin: 1}})
	work := in.withFields()
	depth, _, err := roomLimits(work.RoofDistance)
	if err != nil {
		t.Fatal(err)
	}
	scorer := newPlanScorer(work.Zones, work.Reservations, work.Survey)
	e := newEmbedder(work, depth, scorer, obstacleNone)
	big, small := e.nRooms, e.nRooms+1
	at := Rectangle{X: 100, Z: 50, Width: 20, Height: 20}
	cb := e.blockCost(big, at, make([]Rectangle, len(work.Groups)))
	cs := e.blockCost(small, Rectangle{X: 108, Z: 58, Width: 4, Height: 4}, make([]Rectangle, len(work.Groups)))
	if cs == 0 || cb/cs > 2 || cs/cb > 2 {
		t.Errorf("field cell terms: 400 cells %.1f, 16 cells %.1f", cb, cs)
	}
	room := e.blockCost(0, Rectangle{X: 100, Z: 50, Width: 4, Height: 4}, make([]Rectangle, len(work.Groups)))
	roomBig := e.blockCost(0, Rectangle{X: 100, Z: 50, Width: 20, Height: 20}, make([]Rectangle, len(work.Groups)))
	if roomBig/room < 5 {
		t.Errorf("a room's cell terms scale with its area: %.1f vs %.1f", roomBig, room)
	}
}

// With fields as nodes the embedder no longer bans field zones to rooms.
func TestCoreObstaclesFieldBanIsOptional(t *testing.T) {
	s := soilPatchSurvey()
	for i := range s.Cells {
		s.Cells[i].Fertility = min(s.Cells[i].Fertility, 1) // plain soil, not rich: only the field ban holds it
	}
	zones := Zone(s)
	g := newPlanScorer(zones, nil, s).g
	cell := domain.Cell{X: 110, Z: 60}
	if !g.coreObstaclesFor(zones, obstacleAll, true)[cell] || !g.coreObstacles(zones, obstacleAll)[cell] {
		t.Fatal("the field cell is an obstacle by default")
	}
	if g.coreObstaclesFor(zones, obstacleAll, false)[cell] {
		t.Error("the field ban was not lifted")
	}
}

// The tiler writes the placed field as a field zone, no wall or door, and
// leaves out fogged cells.
func TestTileWritesFieldZone(t *testing.T) {
	in, _ := fieldEmbedInput(t, 20, 100)
	emb, err := Embed(in)
	if err != nil {
		t.Fatal(err)
	}
	res, err := TileEmbedding(TileInput{Groups: in.Groups, Embedding: emb, Catalog: clusterCatalog(), Zones: in.Zones, Survey: in.Survey})
	if err != nil {
		t.Fatal(err)
	}
	field, _ := emb.Field("field/Plant_Rice")
	var zoned []domain.Cell
	for _, z := range res.Plan.Zones[len(in.Zones):] {
		if z.Kind != ZoneField {
			t.Fatalf("zone %+v", z)
		}
		for _, r := range z.Runs {
			for x := r.X; x < r.X+r.Length; x++ {
				zoned = append(zoned, domain.Cell{X: x, Z: r.Z})
			}
		}
	}
	want := cellsOf(field)
	slices.SortFunc(zoned, func(a, b domain.Cell) int { return int(a.Z-b.Z)*1000 + int(a.X-b.X) })
	if !reflect.DeepEqual(zoned, want) {
		t.Errorf("zone cells %d, want the field's %d", len(zoned), len(want))
	}
	if len(res.Plan.Rooms) != len(emb.Blocks) {
		t.Errorf("rooms %d for %d blocks: a field is no room", len(res.Plan.Rooms), len(emb.Blocks))
	}
	fogged := soilPatchSurvey()
	fogged.Cells = slices.DeleteFunc(fogged.Cells, func(c SurveyCell) bool { return c.Cell == (domain.Cell{X: field.X, Z: field.Z}) })
	if z := fieldZone(field, fogged); zoneArea(z) != len(want)-1 {
		t.Errorf("fogged cell kept in the zone: %d cells", zoneArea(z))
	}
}

func zoneArea(z LayoutZone) int {
	n := 0
	for _, r := range z.Runs {
		n += int(r.Length)
	}
	return n
}

// FieldEmbedLinks moves a field's crop-flow edges onto the groups holding the
// stations, splitting a node's weight over the groups that share it.
func TestFieldEmbedLinksSplitOverGroups(t *testing.T) {
	groups := []RoomGroup{
		{ID: 0, Stations: []GroupStation{{Node: "Bench/work", Count: 1}}},
		{ID: 1, Stations: []GroupStation{{Node: "Bench/work", Count: 3}, {Node: "Shelf/store", Count: 1}}},
	}
	g := StationGraph{
		Fields: []FieldNode{{ID: "field/a"}},
		Edges: []StationEdge{
			{A: "Bench/work", B: "field/a", Weight: 8},
			{A: "Shelf/store", B: "field/a", Weight: 5},
		},
	}
	got := FieldEmbedLinks(g, groups)
	want := []GroupLink{{To: 0, Weight: 2}, {To: 1, Weight: 11}}
	if len(got) != 1 || !reflect.DeepEqual(got[0].Links, want) {
		t.Errorf("links %+v, want %+v", got, want)
	}
}

// The advisory chain feeds crops through to a placed field.
func TestAdviseStationsPlacesFieldsForCrops(t *testing.T) {
	f := stationFactsFixture()
	f.Survey = soilPatchSurvey()
	f.Plan.Zones = Zone(f.Survey)
	f.Crops = []CropChoice{riceCrop()}
	f.FoodReserveDays = DefaultFoodReserveDays
	advice, err := TileStations(f)
	if err != nil {
		t.Fatal(err)
	}
	if len(advice.Graph.Fields) != 1 || len(advice.Embedding.Fields) != 1 {
		t.Fatalf("graph fields %d, placed %d: %+v", len(advice.Graph.Fields), len(advice.Embedding.Fields), advice.Embedding.Decisions)
	}
	if advice.Tiled == nil || len(advice.Tiled.Plan.Zones) <= len(f.Plan.Zones) {
		t.Error("the tiled plan holds no field zone")
	}
}
