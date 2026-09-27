package snapshot

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
)

func codes(b ...byte) *mp.FieldArray { return &mp.FieldArray{Form: &mp.FieldArray_Codes{Codes: b}} }
func sparse(index []uint32, code []uint32, number []float64) *mp.FieldArray {
	return &mp.FieldArray{Form: &mp.FieldArray_Sparse{Sparse: &mp.SparseArray{Index: index, Code: code, Number: number}}}
}
func empty() *mp.FieldArray { return sparse(nil, nil, nil) }

// keyframeGrid is a 2x1 window: cell (5,7) held, (6,7) fogged.
func keyframeGrid() *mp.CellGrid {
	g := &mp.CellGrid{Rect: wireRect(policy.Rectangle{X: 5, Z: 7, Width: 2, Height: 1}), Strings: []string{"RoofConstructed", "", "Wall"}}
	g.Cell = codes(1, 0)
	for _, set := range []**mp.FieldArray{&g.Walkable, &g.Occupied, &g.Zone, &g.Roofed, &g.Indoors, &g.SupportsLight, &g.StorageEmpty, &g.Doorway, &g.Polluted, &g.NaturalRock, &g.Ruin} {
		*set = codes(1, 0)
	}
	g.Walkable = codes(2, 0)
	g.Fertility = empty()
	g.Glow = &mp.FieldArray{Form: &mp.FieldArray_Numbers{Numbers: &mp.PackedDouble{Values: []float64{0.25, math.NaN()}}}}
	g.Roof = sparse([]uint32{0}, []uint32{1}, nil)
	g.ZoneId = empty()
	g.PlayerEdifice = sparse([]uint32{0}, []uint32{2}, nil)
	g.ClaimableRuin = sparse([]uint32{0}, []uint32{2}, nil)
	g.RuinHold = empty()
	return g
}

func TestApplyCellGridKeyframeAndDelta(t *testing.T) {
	grid, err := applyCellGrid(nil, true, keyframeGrid())
	if err != nil {
		t.Fatal(err)
	}
	cells := grid.Cells()
	if len(cells) != 1 {
		t.Fatalf("cells = %d, want the one held", len(cells))
	}
	got := cells[0]
	want := policy.SiteCell{Cell: domain.Cell{X: 5, Z: 7}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), Roofed: domain.Known(false),
		Indoors: domain.Known(false), SupportsLight: domain.Known(false), StorageEmpty: domain.Known(false), Doorway: domain.Known(false), Polluted: domain.Known(false),
		NaturalRock: domain.Known(false), Ruin: domain.Known(false), Glow: domain.Known(0.25), Roof: domain.Known("RoofConstructed"), PlayerEdifice: domain.Known(""), ClaimableRuin: domain.Known("")}
	if got != want {
		t.Fatalf("cell = %+v\nwant %+v", got, want)
	}

	// A delta carries only the changed arrays, sparse over the held ones.
	delta := &mp.CellGrid{Rect: wireRect(grid.Rect), Strings: []string{"Wall", "7"}, Cell: sparse([]uint32{1}, []uint32{1}, nil), PlayerEdifice: sparse([]uint32{0}, []uint32{1}, nil), ZoneId: sparse([]uint32{1}, []uint32{2}, nil)}
	next, err := applyCellGrid(grid, false, delta)
	if err != nil {
		t.Fatal(err)
	}
	cells = next.Cells()
	if len(cells) != 2 || cells[0].PlayerEdifice != domain.Known("Wall") || cells[0].Walkable != domain.Known(true) || cells[1].Cell != (domain.Cell{X: 6, Z: 7}) || cells[1].ZoneID != domain.Known("7") || cells[1].Walkable != domain.Unknown[bool]() {
		t.Fatalf("delta cells = %+v", cells)
	}
}

func TestApplyCellGridRefusesMalformed(t *testing.T) {
	missing := keyframeGrid()
	missing.Glow = nil
	if _, err := applyCellGrid(nil, true, missing); err == nil {
		t.Fatal("keyframe without an array applied")
	}
	short := keyframeGrid()
	short.Walkable = codes(2)
	if _, err := applyCellGrid(nil, true, short); err == nil {
		t.Fatal("short array applied")
	}
	index := keyframeGrid()
	index.Roof = sparse([]uint32{0}, []uint32{9}, nil)
	if _, err := applyCellGrid(nil, true, index); err == nil {
		t.Fatal("string index past the table applied")
	}
	held, _ := applyCellGrid(nil, true, keyframeGrid())
	moved := &mp.CellGrid{Rect: wireRect(policy.Rectangle{X: 6, Z: 7, Width: 2, Height: 1})}
	if _, err := applyCellGrid(held, false, moved); err == nil {
		t.Fatal("delta on another rect applied")
	}
}
