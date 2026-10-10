package bridge

import (
	"context"
	"errors"
	"math"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// nativeCell is one map cell as native reads it: the facts both the
// get_cells band (NativeObservationTools.ReadCells) and the grid
// (CellGridEncoder.Read) derive their rows from.
type nativeCell struct {
	fogged                                             bool
	walkable, occupied, doorway, storageEmpty, indoors bool
	polluted, naturalRock                              bool
	roof, zone, room                                   string
	glow, fertility                                    float64 // glow artificial only
	terrain, baseTerrain                               string
	inHome, topRemovable                               bool
	snow                                               float64
	things                                             []policy.Thing
}

// gridMap is a 4x3 map: x 0..3, z 0..2.
const gridMapWidth, gridMapHeight = 4, 3

func gridTestMap() []nativeCell {
	cells := make([]nativeCell, gridMapWidth*gridMapHeight)
	for j := range cells {
		cells[j] = nativeCell{walkable: true, storageEmpty: true, fertility: 1, room: "1", terrain: "Soil", baseTerrain: "Soil", snow: 0.25, inHome: j%2 == 0}
	}
	rice := policy.Thing{Def: "Plant_Rice", Category: policy.ThingPlant, ID: 5, Count: 1, Plant: policy.PlantState{Growth: 0.5}}
	wall := policy.Thing{Def: "Wall", Category: policy.ThingBuilding, Faction: policy.FactionPlayer, Flags: policy.FlagEdifice | policy.FlagImpassable, ID: 6, Count: 1, Building: &policy.BuildingState{HitPoints: 300}}
	cells[3].things = []policy.Thing{rice}
	cells[9].things = []policy.Thing{rice, wall}
	cells[1] = nativeCell{fogged: true}                                                                                  // (1,0)
	cells[2] = nativeCell{walkable: true, roof: "RoofConstructed", indoors: true, room: "4", glow: 0.42, zone: "Zone_9"} // (2,0) roofed, lit
	cells[5] = nativeCell{occupied: true, roof: "RoofConstructed"}                                                       // (1,1) wall
	cells[6] = nativeCell{walkable: true, occupied: true, doorway: true, room: "", glow: 0.6}                            // (2,1) door, art > sky
	cells[7] = nativeCell{occupied: true, naturalRock: true, roof: "RoofRockThick"}                                      // (3,1)
	cells[10] = nativeCell{occupied: true, polluted: true, fertility: 0.7, room: "1"}                                    // (2,2) ruin
	// The old occupied and natural-rock flags are things now.
	for j := range cells {
		switch {
		case cells[j].naturalRock:
			cells[j].things = append(cells[j].things, policy.RockThings(true)...)
		case cells[j].occupied && !policy.SiteCell{Things: cells[j].things}.Occupied():
			cells[j].things = append(cells[j].things, policy.OccupantThings(true)...)
		}
	}
	return cells
}

// gridBand is the site cells over rect a planner reads from native's
// cells, row-major, and the count of fogged cells, glow total at sky.
func gridBand(cells []nativeCell, rect policy.Rectangle, sky float64) ([]policy.SiteCell, uint64) {
	var out []policy.SiteCell
	var fogged uint64
	named := func(s string) domain.Fact[string] {
		if s == "" {
			return domain.Unknown[string]()
		}
		return domain.Known(s)
	}
	for z := rect.Z; z < rect.Z+rect.Height; z++ {
		for x := rect.X; x < rect.X+rect.Width; x++ {
			n := cells[int(z)*gridMapWidth+int(x)]
			if n.fogged {
				fogged++
				continue
			}
			glow := n.glow
			if n.roof == "" {
				glow = math.Max(glow, sky)
			}
			cell := policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(n.walkable), Doorway: domain.Known(n.doorway),
				StorageEmpty: domain.Known(n.storageEmpty),
				Indoors:      domain.Known(n.indoors), Polluted: domain.Known(n.polluted), Glow: domain.Known(glow), Zone: domain.Known(n.zone != ""), Roofed: domain.Known(n.roof != ""),
				Roof: named(n.roof), ZoneID: named(n.zone), Room: named(n.room),
				Terrain: named(n.terrain), InHome: domain.Known(n.inHome), BaseTerrain: named(n.baseTerrain), SnowDepth: domain.Known(n.snow), TopLayerRemovable: domain.Known(n.topRemovable), Things: n.things}
			if n.fertility > 0 {
				cell.Fertility = domain.Known(n.fertility)
			}
			out = append(out, cell)
		}
	}
	return out, fogged
}

// gridWire is the whole map as native's keyframe: dense arrays, read as
// CellGridEncoder.Read does.
func gridWire(cells []nativeCell) *mp.CellGrid {
	g := &mp.CellGrid{Rect: &mp.CellRect{X: proto.Int32(0), Z: proto.Int32(0), Width: proto.Int32(gridMapWidth), Height: proto.Int32(gridMapHeight)}}
	table := map[string]uint32{}
	index := func(s string, known bool) uint32 {
		if !known {
			return 0
		}
		if k, ok := table[s]; ok {
			return k
		}
		g.Strings = append(g.Strings, s)
		table[s] = uint32(len(g.Strings))
		return table[s]
	}
	code := func(f func(nativeCell) bool) *mp.FieldArray {
		out := make([]byte, len(cells))
		for j, n := range cells {
			if !n.fogged {
				out[j] = 1
				if f(n) {
					out[j] = 2
				}
			}
		}
		return &mp.FieldArray{Form: &mp.FieldArray_Codes{Codes: out}}
	}
	number := func(f func(nativeCell) (float64, bool)) *mp.FieldArray {
		out := make([]float64, len(cells))
		for j, n := range cells {
			out[j] = math.NaN()
			if x, ok := f(n); ok && !n.fogged {
				out[j] = x
			}
		}
		return &mp.FieldArray{Form: &mp.FieldArray_Numbers{Numbers: &mp.PackedDouble{Values: out}}}
	}
	str := func(f func(nativeCell) (string, bool)) *mp.FieldArray {
		out := make([]uint32, len(cells))
		for j, n := range cells {
			if s, ok := f(n); ok && !n.fogged {
				out[j] = index(s, true)
			}
		}
		return &mp.FieldArray{Form: &mp.FieldArray_Indexes{Indexes: &mp.PackedUint32{Values: out}}}
	}
	named := func(f func(nativeCell) string) func(nativeCell) (string, bool) {
		return func(n nativeCell) (string, bool) { return f(n), f(n) != "" }
	}
	presence := make([]byte, len(cells))
	for j, n := range cells {
		if !n.fogged {
			presence[j] = 1
		}
	}
	g.Cell = &mp.FieldArray{Form: &mp.FieldArray_Codes{Codes: presence}}
	g.Walkable = code(func(n nativeCell) bool { return n.walkable })
	g.Zone = code(func(n nativeCell) bool { return n.zone != "" })
	g.Roofed = code(func(n nativeCell) bool { return n.roof != "" })
	g.Indoors = code(func(n nativeCell) bool { return n.indoors })
	g.StorageEmpty = code(func(n nativeCell) bool { return n.storageEmpty })
	g.Doorway = code(func(n nativeCell) bool { return n.doorway })
	g.Fertility = number(func(n nativeCell) (float64, bool) { return n.fertility, n.fertility > 0 })
	g.Polluted = code(func(n nativeCell) bool { return n.polluted })
	g.Glow = number(func(n nativeCell) (float64, bool) { return n.glow, true })
	g.Roof = str(named(func(n nativeCell) string { return n.roof }))
	g.ZoneId = str(named(func(n nativeCell) string { return n.zone }))
	g.Room = str(named(func(n nativeCell) string { return n.room }))
	g.Terrain = str(named(func(n nativeCell) string { return n.terrain }))
	g.InHome = code(func(n nativeCell) bool { return n.inHome })
	g.BaseTerrain = str(named(func(n nativeCell) string { return n.baseTerrain }))
	g.SnowDepth = number(func(n nativeCell) (float64, bool) { return n.snow, true })
	g.TopLayerRemovable = code(func(n nativeCell) bool { return n.topRemovable })
	g.Things = thingList(cells, index)
	return g
}

// thingList is the cells' things as native's keyframe lists them: the
// cells holding any, each replaced in full. The test things are plants and
// walls, the two states it needs.
func thingList(cells []nativeCell, index func(string, bool) uint32) *mp.ThingList {
	list := &mp.ThingList{Offsets: []uint32{0}}
	for j, n := range cells {
		if len(n.things) == 0 {
			continue
		}
		list.Cells = append(list.Cells, uint32(j))
		for _, t := range n.things {
			w := &mp.Thing{Def: index(t.Def, true), Faction: mp.ThingFaction(t.Faction), Flags: uint32(t.Flags), Id: t.ID, Count: t.Count}
			if t.Category == policy.ThingPlant {
				w.Category = mp.ThingCategory_THING_CATEGORY_PLANT
				w.State = &mp.Thing_Plant{Plant: &mp.PlantState{Growth: t.Plant.Growth, Blighted: t.Plant.Blighted}}
			} else {
				w.Category = mp.ThingCategory_THING_CATEGORY_BUILDING
				w.State = &mp.Thing_Building{Building: &mp.BuildingState{HitPoints: t.Building.HitPoints}}
			}
			list.Things = append(list.Things, w)
		}
		list.Offsets = append(list.Offsets, uint32(len(list.Things)))
	}
	return list
}

func gridFrame(base *o.BundleSnapshot, tick int64, grid *mp.CellGrid, seq uint64, sky float64) *o.BundleSnapshot {
	v := sectionFrame(base, tick, nil, "emergency", "colony_facts", "population", "research", "traders", "world_progression")
	v.Grid, v.KeyframeSeq, v.SkyGlow = grid, proto.Uint64(seq), proto.Float64(sky)
	return v
}

// TestFrameGridMatchesTheBand checks that the planning window
// served from a frame's grid, keyframe and then a cumulative delta, is
// the site cells native's cells hold over the window's rect,
// glow compared as artificial light raised to the sky on unroofed cells.
func TestFrameGridMatchesTheBand(t *testing.T) {
	client, server, ring := frameClient(t)
	rect := policy.Rectangle{X: 1, Z: 0, Width: 3, Height: 3}
	cells := gridTestMap()
	check := func(label string, sky float64) {
		t.Helper()
		window, _, err := client.ReadPlanningWindow(context.Background(), pbIdentity(), rect)
		if err != nil {
			t.Fatalf("%s: %v", label, err)
		}
		want, filtered := gridBand(cells, rect, sky)
		if window.Filtered != filtered || len(window.Cells) != len(want) {
			t.Fatalf("%s: %d cells %d fogged, band %d cells %d fogged", label, len(window.Cells), window.Filtered, len(want), filtered)
		}
		for i := range want {
			if !window.Cells[i].Equal(want[i]) {
				t.Fatalf("%s cell %v:\n grid %+v\n band %+v", label, want[i].Cell, window.Cells[i], want[i])
			}
		}
		if window.Region != rect {
			t.Fatalf("%s: region %+v", label, window.Region)
		}
	}
	ring.publish(t, gridFrame(server.snapshot, 12, gridWire(cells), 1, 0.8), 0)
	check("keyframe by day", 0.8)

	// A wall goes up at (2,2) (was the ruin): the delta, against the
	// keyframe, carries only the arrays that changed, sparse.
	key := gridWire(cells)
	cells[10] = nativeCell{fertility: 0.7, things: policy.OccupantThings(true)}
	next := gridWire(cells)
	delta := &mp.CellGrid{Rect: next.Rect, Strings: next.Strings, Things: next.Things}
	delta.Walkable = &mp.FieldArray{Form: &mp.FieldArray_Sparse{Sparse: &mp.SparseArray{Index: []uint32{10}, Code: []uint32{1}}}}
	delta.StorageEmpty = &mp.FieldArray{Form: &mp.FieldArray_Sparse{Sparse: &mp.SparseArray{Index: []uint32{10}, Code: []uint32{1}}}}
	delta.Polluted = &mp.FieldArray{Form: &mp.FieldArray_Sparse{Sparse: &mp.SparseArray{Index: []uint32{10}, Code: []uint32{1}}}}
	delta.Room = &mp.FieldArray{Form: &mp.FieldArray_Sparse{Sparse: &mp.SparseArray{Index: []uint32{10}, Code: []uint32{0}}}}
	if proto.Equal(key, next) {
		t.Fatal("the wall changed nothing")
	}
	ring.publish(t, gridFrame(server.snapshot, 13, delta, 1, 0), 0)
	check("delta at night", 0)
	// A later delta is against the keyframe, not the frame before.
	ring.publish(t, gridFrame(server.snapshot, 14, delta, 1, 0.3), 0)
	check("same delta again", 0.3)

	// A delta against a keyframe this reader never saw: no grid, and a
	// keyframe request.
	<-server.opens
	ring.publish(t, gridFrame(server.snapshot, 15, delta, 2, 0.3), 0)
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	if _, err := client.frameWindow(ctx, pbIdentity(), rect); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("delta after a missed keyframe: %v, want unavailable", err)
	}
	select {
	case request := <-server.opens:
		if !request.GetKeyframe() {
			t.Fatalf("open %v, want a keyframe request", request)
		}
	case <-time.After(time.Second):
		t.Fatal("no keyframe requested")
	}
	ring.publish(t, gridFrame(server.snapshot, 16, next, 2, 0.3), 0)
	check("new keyframe", 0.3)
}
