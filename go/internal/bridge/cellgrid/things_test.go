package cellgrid

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

func thingCells(things map[domain.Cell][]policy.Thing) map[domain.Cell]policy.SiteCell {
	cells := map[domain.Cell]policy.SiteCell{}
	for z := int32(7); z < 9; z++ {
		for x := int32(5); x < 8; x++ {
			c := domain.Cell{X: x, Z: z}
			cells[c] = policy.SiteCell{Cell: c, Walkable: domain.Known(true), Terrain: domain.Known("Soil"), InHome: domain.Known(x == 5),
				SnowDepth: domain.Known(0.5), TopLayerRemovable: domain.Known(false), FoundationAffordances: domain.Known("Heavy,Light"), Things: things[c]}
		}
	}
	return cells
}

func someThings() map[domain.Cell][]policy.Thing {
	wall := policy.Thing{Def: "Wall", Category: policy.ThingBuilding, Faction: policy.FactionPlayer, Flags: policy.FlagEdifice | policy.FlagImpassable | policy.FlagHoldsRoof | policy.FlagDeconstructible, ID: 11, Count: 1,
		Building: &policy.BuildingState{HitPoints: 300, Burning: true}}
	frame := policy.Thing{Def: "Frame_Door", Category: policy.ThingBuilding, Flags: policy.FlagFrame, ID: 12, Count: 1,
		Building: &policy.BuildingState{HitPoints: 20, Reserved: true, Needed: []policy.Material{{Def: "Steel", Count: 25}, {Def: "WoodLog", Count: 3}}}}
	casket := policy.Thing{Def: "Sarcophagus", Category: policy.ThingBuilding, ID: 13, Count: 1, Building: &policy.BuildingState{HitPoints: 80, Casket: []string{"Corpse_Human"}}}
	plant := policy.Thing{Def: "Plant_Rice", Category: policy.ThingPlant, ID: 21, Count: 1, Plant: policy.PlantState{Growth: 0.5, Blighted: true}}
	corpse := policy.Thing{Def: "Corpse_Muffalo", Category: policy.ThingCorpse, Flags: policy.FlagHaulable, ID: 22, Count: 1, Corpse: policy.CorpseState{Class: policy.CorpseAnimal, Rot: 0.25}}
	filth := policy.Thing{Def: "Filth_Dirt", Category: policy.ThingFilth, ID: 23, Count: 1, FilthThickness: 3}
	steel := policy.Thing{Def: "Steel", Category: policy.ThingItem, Flags: policy.FlagHaulable | policy.FlagForbidden, ID: 24, Count: 75, ItemDeterioration: 0.125}
	ruin := policy.Thing{Def: "AncientWall", Category: policy.ThingOther, Flags: policy.FlagEdifice | policy.FlagAncientDanger | policy.FlagClaimable, ID: 25, Count: 1}
	return map[domain.Cell][]policy.Thing{
		{X: 5, Z: 7}: {wall},
		{X: 6, Z: 7}: {frame, plant, filth},
		{X: 7, Z: 7}: {casket, corpse},
		{X: 5, Z: 8}: {steel, ruin},
	}
}

func equalCells(t *testing.T, got, want []policy.SiteCell) {
	t.Helper()
	if len(got) != len(want) {
		t.Fatalf("%d cells, want %d", len(got), len(want))
	}
	for i := range want {
		if !got[i].Equal(want[i]) {
			t.Fatalf("cell %v:\n got %+v\nwant %+v", want[i].Cell, got[i], want[i])
		}
	}
}

func sortedCells(cells map[domain.Cell]policy.SiteCell) []policy.SiteCell {
	var out []policy.SiteCell
	for z := int32(7); z < 9; z++ {
		for x := int32(5); x < 8; x++ {
			out = append(out, cells[domain.Cell{X: x, Z: z}])
		}
	}
	return out
}

// marshal round-trips a wire grid through bytes, as the stream does.
func marshal(t *testing.T, g *mp.CellGrid) *mp.CellGrid {
	t.Helper()
	data, err := proto.Marshal(g)
	if err != nil {
		t.Fatal(err)
	}
	var out mp.CellGrid
	if err := proto.Unmarshal(data, &out); err != nil {
		t.Fatal(err)
	}
	return &out
}

func TestThingsKeyframeAndDeltaRoundTrip(t *testing.T) {
	things := someThings()
	key, err := FromCells(thingCells(things), 100)
	if err != nil {
		t.Fatal(err)
	}
	wire := marshal(t, key.Wire(nil))
	held, err := Apply(nil, true, wire)
	if err != nil {
		t.Fatal(err)
	}
	equalCells(t, held.Cells(), sortedCells(thingCells(things)))

	// A delta replaces whole lists on the cells that differ from the
	// keyframe: (5,7) loses its wall, (6,8) gains a plant, (6,7) is
	// untouched.
	next := someThings()
	delete(next, domain.Cell{X: 5, Z: 7})
	next[domain.Cell{X: 6, Z: 8}] = []policy.Thing{{Def: "Plant_Rice", Category: policy.ThingPlant, ID: 31, Count: 1}}
	nextGrid, _ := FromCells(thingCells(next), 100)
	deltaWire := marshal(t, nextGrid.Wire(key))
	list := deltaWire.Things
	if len(list.Cells) != 2 || list.Cells[0] != 0 || list.Cells[1] != 4 || len(list.Things) != 1 {
		t.Fatalf("delta list = %v, want cells 0 and 4 and one thing", list)
	}
	if deltaWire.Walkable != nil || deltaWire.Terrain != nil {
		t.Fatal("delta carries unchanged tile columns")
	}
	applied, err := Apply(held, false, deltaWire)
	if err != nil {
		t.Fatal(err)
	}
	equalCells(t, applied.Cells(), sortedCells(thingCells(next)))
	// The base is untouched, and an unchanged grid shares its store.
	equalCells(t, held.Cells(), sortedCells(thingCells(things)))
	same, _ := FromCells(thingCells(next), 100)
	if w := same.Wire(nextGrid); w.Things != nil {
		t.Fatalf("an unchanged grid wires things: %v", w.Things)
	}
	// A delta without a thing list shares the held store.
	bare, err := Apply(held, false, &mp.CellGrid{Rect: WireRect(held.Rect), Terrain: sparse([]uint32{0}, []uint32{1}, nil), Strings: []string{"Sand"}})
	if err != nil {
		t.Fatal(err)
	}
	if bare.things != held.things {
		t.Fatal("a delta without things copied the store")
	}
	if got := bare.Cells()[0].Terrain; got != domain.Known("Sand") {
		t.Fatalf("terrain = %v", got)
	}
	// The window serves the same lists.
	window, fogged := applied.Window(policy.Rectangle{X: 6, Z: 7, Width: 2, Height: 2}, 0)
	if fogged != 0 || len(window) != 4 || len(window[0].Things) != 3 {
		t.Fatalf("window %d cells, %d fogged, first things %d", len(window), fogged, len(window[0].Things))
	}
}

func TestThingsFoggedIsNotEmpty(t *testing.T) {
	g := keyframeGrid()
	g.Things = &mp.ThingList{Cells: []uint32{0}, Offsets: []uint32{0, 0}}
	grid, err := Apply(nil, true, g)
	if err != nil {
		t.Fatal(err)
	}
	cells := grid.Cells()
	if len(cells) != 1 || cells[0].Things != nil {
		t.Fatalf("cells = %+v: want the held cell with an empty list and the fogged one absent", cells)
	}
	if w, fogged := grid.Window(grid.Rect, 0); len(w) != 1 || fogged != 1 {
		t.Fatalf("window %d cells %d fogged", len(w), fogged)
	}
}

func TestKeyframeOfUnknownTileColumnsHoldsNone(t *testing.T) {
	grid, err := Apply(nil, true, keyframeGrid())
	if err != nil {
		t.Fatal(err)
	}
	c := grid.Cells()[0]
	if len(c.Things) != 0 || c.Terrain != domain.Unknown[string]() || c.InHome != domain.Unknown[bool]() || c.SnowDepth != domain.Unknown[float64]() {
		t.Fatalf("cell = %+v", c)
	}
	if !Complete(keyframeGrid()) {
		t.Fatal("a keyframe with every column is not complete")
	}
	g := keyframeGrid()
	g.Things = nil
	if Complete(g) {
		t.Fatal("a keyframe without its thing list is complete")
	}
}

func TestThingsRefuseMalformed(t *testing.T) {
	thing := func(def uint32, cat mp.ThingCategory, state func(*mp.Thing)) *mp.Thing {
		w := &mp.Thing{Def: def, Category: cat}
		if state != nil {
			state(w)
		}
		return w
	}
	plant := func(w *mp.Thing) { w.State = &mp.Thing_Plant{Plant: &mp.PlantState{Growth: 1}} }
	cases := map[string]*mp.ThingList{
		"non-monotonic offsets": {Cells: []uint32{0, 1}, Offsets: []uint32{0, 2, 1}, Things: []*mp.Thing{
			thing(1, mp.ThingCategory_THING_CATEGORY_OTHER, nil)}},
		"offsets not from zero":    {Cells: []uint32{0}, Offsets: []uint32{1, 1}},
		"offsets short":            {Cells: []uint32{0, 1}, Offsets: []uint32{0, 0}},
		"offsets past the things":  {Cells: []uint32{0}, Offsets: []uint32{0, 1}},
		"cells descending":         {Cells: []uint32{1, 0}, Offsets: []uint32{0, 0, 0}},
		"cell past the rect":       {Cells: []uint32{2}, Offsets: []uint32{0, 0}},
		"bad def index":            {Cells: []uint32{0}, Offsets: []uint32{0, 1}, Things: []*mp.Thing{thing(9, mp.ThingCategory_THING_CATEGORY_OTHER, nil)}},
		"zero def index":           {Cells: []uint32{0}, Offsets: []uint32{0, 1}, Things: []*mp.Thing{thing(0, mp.ThingCategory_THING_CATEGORY_OTHER, nil)}},
		"unspecified category":     {Cells: []uint32{0}, Offsets: []uint32{0, 1}, Things: []*mp.Thing{thing(1, mp.ThingCategory_THING_CATEGORY_UNSPECIFIED, nil)}},
		"state without a category": {Cells: []uint32{0}, Offsets: []uint32{0, 1}, Things: []*mp.Thing{thing(1, mp.ThingCategory_THING_CATEGORY_OTHER, plant)}},
		"oneof category mismatch":  {Cells: []uint32{0}, Offsets: []uint32{0, 1}, Things: []*mp.Thing{thing(1, mp.ThingCategory_THING_CATEGORY_BUILDING, plant)}},
		"typed category no state":  {Cells: []uint32{0}, Offsets: []uint32{0, 1}, Things: []*mp.Thing{thing(1, mp.ThingCategory_THING_CATEGORY_PLANT, nil)}},
		"unknown flag bit":         {Cells: []uint32{0}, Offsets: []uint32{0, 1}, Things: []*mp.Thing{{Def: 1, Category: mp.ThingCategory_THING_CATEGORY_OTHER, Flags: 1 << 20}}},
		"bad faction":              {Cells: []uint32{0}, Offsets: []uint32{0, 1}, Things: []*mp.Thing{{Def: 1, Category: mp.ThingCategory_THING_CATEGORY_OTHER, Faction: 9}}},
		"bad material def": {Cells: []uint32{0}, Offsets: []uint32{0, 1}, Things: []*mp.Thing{thing(1, mp.ThingCategory_THING_CATEGORY_BUILDING, func(w *mp.Thing) {
			w.State = &mp.Thing_Building{Building: &mp.BuildingState{Needed: []*mp.MaterialNeed{{Def: 9, Count: 1}}}}
		})}},
		"bad casket def": {Cells: []uint32{0}, Offsets: []uint32{0, 1}, Things: []*mp.Thing{thing(1, mp.ThingCategory_THING_CATEGORY_BUILDING, func(w *mp.Thing) {
			w.State = &mp.Thing_Building{Building: &mp.BuildingState{Casket: []uint32{9}}}
		})}},
	}
	for name, list := range cases {
		g := keyframeGrid()
		g.Things = list
		if _, err := Apply(nil, true, g); err == nil {
			t.Errorf("%s: applied", name)
		}
	}
	// A bad delta list is refused over a held grid too.
	held, _ := Apply(nil, true, keyframeGrid())
	delta := &mp.CellGrid{Rect: WireRect(held.Rect), Things: cases["bad def index"]}
	if _, err := Apply(held, false, delta); err == nil {
		t.Error("bad delta list applied")
	}
}
