package bridge

import (
	"context"
	"errors"
	"math"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// nativeCell is one map cell as native reads it: the facts both the
// get_cells band (NativeObservationTools.ReadCells) and the grid
// (CellGridEncoder.Read) derive their rows from.
type nativeCell struct {
	fogged                                                    bool
	walkable, occupied, doorway, light, storageEmpty, indoors bool
	polluted, naturalRock, ruin                               bool
	roof, zone, room, edifice, claimable                      string
	glow, fertility                                           float64 // glow artificial only
}

// gridMap is a 4x3 map: x 0..3, z 0..2.
const gridMapWidth, gridMapHeight = 4, 3

func gridTestMap() []nativeCell {
	cells := make([]nativeCell, gridMapWidth*gridMapHeight)
	for j := range cells {
		cells[j] = nativeCell{walkable: true, light: true, storageEmpty: true, fertility: 1, room: "1"}
	}
	cells[1] = nativeCell{fogged: true}                                                                                               // (1,0)
	cells[2] = nativeCell{walkable: true, light: true, roof: "RoofConstructed", indoors: true, room: "4", glow: 0.42, zone: "Zone_9"} // (2,0) roofed, lit
	cells[5] = nativeCell{occupied: true, edifice: "Wall", light: true, roof: "RoofConstructed"}                                      // (1,1) wall
	cells[6] = nativeCell{walkable: true, occupied: true, doorway: true, light: true, edifice: "Door", room: "", glow: 0.6}           // (2,1) door, art > sky
	cells[7] = nativeCell{occupied: true, naturalRock: true, roof: "RoofRockThick"}                                                   // (3,1)
	cells[10] = nativeCell{occupied: true, ruin: true, claimable: "Wall", polluted: true, fertility: 0.7, room: "1"}                  // (2,2) ruin
	return cells
}

// gridBand is the get_cells band over rect as native compiles it, glow
// total at sky.
func gridBand(cells []nativeCell, rect policy.Rectangle, sky float64) *o.CellsSnapshot {
	v := &o.CellsSnapshot{AppliedFields: planningWindowFields()}
	for z := rect.Z; z < rect.Z+rect.Height; z++ {
		for x := rect.X; x < rect.X+rect.Width; x++ {
			n := cells[int(z)*gridMapWidth+int(x)]
			row := &o.CellState{Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}}
			if n.fogged {
				row.Fogged = proto.Bool(true)
				v.Cells = append(v.Cells, row)
				continue
			}
			glow := n.glow
			if n.roof == "" {
				glow = math.Max(glow, sky)
			}
			row.Walkable, row.Passable, row.Occupied, row.Doorway = proto.Bool(n.walkable), proto.Bool(n.walkable), proto.Bool(n.occupied), proto.Bool(n.doorway)
			row.SupportsLight, row.NaturalRock, row.Ruin = proto.Bool(n.light), proto.Bool(n.naturalRock), proto.Bool(n.ruin)
			row.StorageEmpty, row.Indoors, row.Polluted, row.Glow = proto.Bool(n.storageEmpty), proto.Bool(n.indoors), proto.Bool(n.polluted), proto.Float64(glow)
			for _, f := range []struct {
				value string
				slot  **string
			}{{n.roof, &row.Roof}, {n.zone, &row.ZoneId}, {n.room, &row.RoomId}, {n.edifice, &row.PlayerEdifice}, {n.claimable, &row.ClaimableRuin}} {
				if f.value != "" {
					*f.slot = proto.String(f.value)
				}
			}
			if n.fertility > 0 {
				row.Fertility = proto.Float64(n.fertility)
			}
			v.Cells = append(v.Cells, row)
		}
	}
	return v
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
	g.Occupied = code(func(n nativeCell) bool { return n.occupied })
	g.Zone = code(func(n nativeCell) bool { return n.zone != "" })
	g.Roofed = code(func(n nativeCell) bool { return n.roof != "" })
	g.Indoors = code(func(n nativeCell) bool { return n.indoors })
	g.SupportsLight = code(func(n nativeCell) bool { return n.light })
	g.StorageEmpty = code(func(n nativeCell) bool { return n.storageEmpty })
	g.Doorway = code(func(n nativeCell) bool { return n.doorway })
	g.Fertility = number(func(n nativeCell) (float64, bool) { return n.fertility, n.fertility > 0 })
	g.Polluted = code(func(n nativeCell) bool { return n.polluted })
	g.Glow = number(func(n nativeCell) (float64, bool) { return n.glow, true })
	g.Roof = str(named(func(n nativeCell) string { return n.roof }))
	g.ZoneId = str(named(func(n nativeCell) string { return n.zone }))
	g.NaturalRock = code(func(n nativeCell) bool { return n.naturalRock })
	g.Ruin = code(func(n nativeCell) bool { return n.ruin })
	g.PlayerEdifice = str(func(n nativeCell) (string, bool) { return n.edifice, true })
	g.ClaimableRuin = str(func(n nativeCell) (string, bool) { return n.claimable, true })
	g.RuinHold = str(func(nativeCell) (string, bool) { return "", false })
	g.Room = str(named(func(n nativeCell) string { return n.room }))
	return g
}

func gridFrame(base *o.BundleSnapshot, tick int64, grid *mp.CellGrid, seq uint64, sky float64) *o.BundleSnapshot {
	v := sectionFrame(base, tick, nil, "emergency", "colony_facts", "population", "research", "traders", "world_progression")
	v.Grid, v.KeyframeSeq, v.SkyGlow = grid, proto.Uint64(seq), proto.Float64(sky)
	return v
}

// TestFrameGridMatchesTheBand is #1552's equivalence: the planning window
// served from a frame's grid, keyframe and then a cumulative delta, is
// the site cells the get_cells band decodes to over the band's rect,
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
		band := gridBand(cells, rect, sky)
		want, filtered := PlanningCells(band)
		if window.Filtered != filtered || len(window.Cells) != len(want) {
			t.Fatalf("%s: %d cells %d fogged, band %d cells %d fogged", label, len(window.Cells), window.Filtered, len(want), filtered)
		}
		for i := range want {
			if window.Cells[i] != want[i] {
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
	cells[10] = nativeCell{occupied: true, light: true, edifice: "Wall", fertility: 0.7}
	next := gridWire(cells)
	delta := &mp.CellGrid{Rect: next.Rect, Strings: []string{"Wall", ""}}
	delta.Walkable = &mp.FieldArray{Form: &mp.FieldArray_Sparse{Sparse: &mp.SparseArray{Index: []uint32{10}, Code: []uint32{1}}}}
	delta.SupportsLight = &mp.FieldArray{Form: &mp.FieldArray_Sparse{Sparse: &mp.SparseArray{Index: []uint32{10}, Code: []uint32{2}}}}
	delta.StorageEmpty = &mp.FieldArray{Form: &mp.FieldArray_Sparse{Sparse: &mp.SparseArray{Index: []uint32{10}, Code: []uint32{1}}}}
	delta.Polluted = &mp.FieldArray{Form: &mp.FieldArray_Sparse{Sparse: &mp.SparseArray{Index: []uint32{10}, Code: []uint32{1}}}}
	delta.Ruin = &mp.FieldArray{Form: &mp.FieldArray_Sparse{Sparse: &mp.SparseArray{Index: []uint32{10}, Code: []uint32{1}}}}
	delta.PlayerEdifice = &mp.FieldArray{Form: &mp.FieldArray_Sparse{Sparse: &mp.SparseArray{Index: []uint32{10}, Code: []uint32{1}}}}
	delta.ClaimableRuin = &mp.FieldArray{Form: &mp.FieldArray_Sparse{Sparse: &mp.SparseArray{Index: []uint32{10}, Code: []uint32{2}}}}
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
