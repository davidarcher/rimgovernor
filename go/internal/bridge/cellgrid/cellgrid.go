// Package cellgrid is the mirror.CellGrid format: one array
// per policy.SiteCell field over a rect, with the wire's sentinels (cell 0
// not held, 1 held; bools 0 unknown, 1 false, 2 true; floats NaN unknown;
// strings 0 unknown, k for strings[k-1]). A keyframe carries every array;
// a delta, on the keyframe's rect, only the arrays that differ from the
// base it is applied over, each dense or sparse over that base. The native
// snapshot stream sends the whole map this way (bridge's frame grid) and
// the snapshot recorder stores planning_cells lines in it.
package cellgrid

import (
	"errors"
	"fmt"
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

// MaxCells bounds a grid's rect: a whole map of up to 1024x1024.
const MaxCells = 1 << 20

func contract(msg string) error { return fmt.Errorf("cellgrid: %s", msg) }

type kind uint8

const (
	kindCode   kind = iota // presence and Fact[bool]
	kindNumber             // Fact[float64]
	kindIndex              // Fact[string]
)

// column is one array of a Grid: codes, numbers (NaN unknown) or string
// indexes into the grid's table (0 unknown).
type column struct {
	codes []uint8
	nums  []float64
	idx   []uint32
}

// array is one CellGrid array, in the wire's field order (presence
// first): its kind, its slot on the wire, and a SiteCell's field read
// into (put) and written from (set) a code, a number or a string.
type array struct {
	kind kind
	slot func(*mp.CellGrid) **mp.FieldArray
	// code/num/str read a SiteCell field; known false is the sentinel.
	code func(*policy.SiteCell) uint8
	num  func(*policy.SiteCell) float64
	str  func(*policy.SiteCell) (string, bool)
	// setCode/setNum/setStr write it (str known false: unknown).
	setCode func(*policy.SiteCell, uint8)
	setNum  func(*policy.SiteCell, float64)
	setStr  func(*policy.SiteCell, string, bool)
}

func (a array) wire(g *mp.CellGrid) *mp.FieldArray   { return *a.slot(g) }
func (a array) put(g *mp.CellGrid, f *mp.FieldArray) { *a.slot(g) = f }

func boolArray(slot func(*mp.CellGrid) **mp.FieldArray, field func(*policy.SiteCell) *domain.Fact[bool]) array {
	return array{kind: kindCode, slot: slot,
		code: func(c *policy.SiteCell) uint8 {
			switch b, known := field(c).Value(); {
			case !known:
				return 0
			case b:
				return 2
			}
			return 1
		},
		setCode: func(c *policy.SiteCell, v uint8) {
			if v == 0 {
				*field(c) = domain.Unknown[bool]()
			} else {
				*field(c) = domain.Known(v == 2)
			}
		}}
}

func numArray(slot func(*mp.CellGrid) **mp.FieldArray, field func(*policy.SiteCell) *domain.Fact[float64]) array {
	return array{kind: kindNumber, slot: slot,
		num: func(c *policy.SiteCell) float64 {
			if x, known := field(c).Value(); known {
				return x
			}
			return math.NaN()
		},
		setNum: func(c *policy.SiteCell, v float64) {
			if math.IsNaN(v) {
				*field(c) = domain.Unknown[float64]()
			} else {
				*field(c) = domain.Known(v)
			}
		}}
}

func strArray(slot func(*mp.CellGrid) **mp.FieldArray, field func(*policy.SiteCell) *domain.Fact[string]) array {
	return array{kind: kindIndex, slot: slot,
		str: func(c *policy.SiteCell) (string, bool) { return field(c).Value() },
		setStr: func(c *policy.SiteCell, s string, known bool) {
			if known {
				*field(c) = domain.Known(s)
			} else {
				*field(c) = domain.Unknown[string]()
			}
		}}
}

// Index positions of the arrays read directly.
const (
	presenceArray = 0
	roofedArray   = 3
)

var arrays = []array{
	{kind: kindCode, slot: func(g *mp.CellGrid) **mp.FieldArray { return &g.Cell }, code: func(*policy.SiteCell) uint8 { return 1 }},
	boolArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.Walkable }, func(c *policy.SiteCell) *domain.Fact[bool] { return &c.Walkable }),
	boolArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.Zone }, func(c *policy.SiteCell) *domain.Fact[bool] { return &c.Zone }),
	boolArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.Roofed }, func(c *policy.SiteCell) *domain.Fact[bool] { return &c.Roofed }),
	boolArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.Indoors }, func(c *policy.SiteCell) *domain.Fact[bool] { return &c.Indoors }),
	boolArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.SupportsLight }, func(c *policy.SiteCell) *domain.Fact[bool] { return &c.SupportsLight }),
	boolArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.StorageEmpty }, func(c *policy.SiteCell) *domain.Fact[bool] { return &c.StorageEmpty }),
	boolArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.Doorway }, func(c *policy.SiteCell) *domain.Fact[bool] { return &c.Doorway }),
	numArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.Fertility }, func(c *policy.SiteCell) *domain.Fact[float64] { return &c.Fertility }),
	boolArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.Polluted }, func(c *policy.SiteCell) *domain.Fact[bool] { return &c.Polluted }),
	numArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.Glow }, func(c *policy.SiteCell) *domain.Fact[float64] { return &c.Glow }),
	strArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.Roof }, func(c *policy.SiteCell) *domain.Fact[string] { return &c.Roof }),
	strArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.ZoneId }, func(c *policy.SiteCell) *domain.Fact[string] { return &c.ZoneID }),
	strArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.Room }, func(c *policy.SiteCell) *domain.Fact[string] { return &c.Room }),
	strArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.Terrain }, func(c *policy.SiteCell) *domain.Fact[string] { return &c.Terrain }),
	boolArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.InHome }, func(c *policy.SiteCell) *domain.Fact[bool] { return &c.InHome }),
	strArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.FoundationAffordances }, func(c *policy.SiteCell) *domain.Fact[string] { return &c.FoundationAffordances }),
	numArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.SnowDepth }, func(c *policy.SiteCell) *domain.Fact[float64] { return &c.SnowDepth }),
	boolArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.TopLayerRemovable }, func(c *policy.SiteCell) *domain.Fact[bool] { return &c.TopLayerRemovable }),
}

// Grid is a decoded grid: its rect, every array, and the string table its
// index arrays point into (index k is strings[k-1]). A Grid is never
// changed once built; Apply returns a new one sharing unchanged arrays.
type Grid struct {
	Rect    policy.Rectangle
	cols    []column
	strings []string
	// things is the per-cell thing list; nil holds none anywhere.
	things *thingStore
}

func (g *Grid) str(k uint32) (string, bool) {
	if k == 0 {
		return "", false
	}
	return g.strings[k-1], true
}

// table interns strings into a grid's string table.
type table struct {
	strings *[]string
	index   map[string]uint32
}

func newTable(strings *[]string) table {
	t := table{strings: strings, index: make(map[string]uint32, len(*strings))}
	for i, s := range *strings {
		if _, ok := t.index[s]; !ok {
			t.index[s] = uint32(i + 1)
		}
	}
	return t
}

func (t table) intern(s string, known bool) uint32 {
	if !known {
		return 0
	}
	k, ok := t.index[s]
	if !ok {
		*t.strings = append(*t.strings, s)
		k = uint32(len(*t.strings))
		t.index[s] = k
	}
	return k
}

func newColumn(k kind, n int) column {
	switch k {
	case kindCode:
		return column{codes: make([]uint8, n)}
	case kindNumber:
		nums := make([]float64, n)
		for j := range nums {
			nums[j] = math.NaN()
		}
		return column{nums: nums}
	}
	return column{idx: make([]uint32, n)}
}

// ErrNotGrid reports site cells that do not form a grid FromCells encodes.
var ErrNotGrid = errors.New("cellgrid: rows are not a planning grid")

// FromCells is cells as arrays over their bounding rect, of at most
// maxCells cells.
func FromCells(cells map[domain.Cell]policy.SiteCell, maxCells int64) (*Grid, error) {
	if len(cells) == 0 {
		return nil, ErrNotGrid
	}
	first := true
	var lo, hi domain.Cell
	for c := range cells {
		if first {
			lo, hi, first = c, c, false
		}
		lo.X, lo.Z, hi.X, hi.Z = min(lo.X, c.X), min(lo.Z, c.Z), max(hi.X, c.X), max(hi.Z, c.Z)
	}
	rect := policy.Rectangle{X: lo.X, Z: lo.Z, Width: hi.X - lo.X + 1, Height: hi.Z - lo.Z + 1}
	n := int64(rect.Width) * int64(rect.Height)
	if n > maxCells {
		return nil, ErrNotGrid
	}
	g := &Grid{Rect: rect, cols: make([]column, len(arrays))}
	for i, a := range arrays {
		g.cols[i] = newColumn(a.kind, int(n))
	}
	t := newTable(&g.strings)
	total := 0
	for _, row := range cells {
		total += len(row.Things)
	}
	if total > 0 {
		order := make([]*policy.SiteCell, n)
		for c, row := range cells {
			order[int(c.Z-rect.Z)*int(rect.Width)+int(c.X-rect.X)] = &row
		}
		b := newThingBuilder(int(n), total)
		for _, row := range order {
			if row != nil {
				for _, th := range row.Things {
					b.add(th)
				}
			}
			b.endCell()
		}
		g.things = b.finish()
	}
	for c, row := range cells {
		j := int(c.Z-rect.Z)*int(rect.Width) + int(c.X-rect.X)
		for i, a := range arrays {
			switch a.kind {
			case kindCode:
				g.cols[i].codes[j] = a.code(&row)
			case kindNumber:
				g.cols[i].nums[j] = a.num(&row)
			default:
				g.cols[i].idx[j] = t.intern(a.str(&row))
			}
		}
	}
	return g, nil
}

// same reports whether array i holds the same value at j in g and o.
func (g *Grid) same(o *Grid, i, j int) bool {
	a, b := g.cols[i], o.cols[i]
	switch {
	case a.codes != nil:
		return a.codes[j] == b.codes[j]
	case a.nums != nil:
		return math.Float64bits(a.nums[j]) == math.Float64bits(b.nums[j])
	}
	s, sk := g.str(a.idx[j])
	t, tk := o.str(b.idx[j])
	return s == t && sk == tk
}

func (g *Grid) sentinel(i, j int) bool {
	c := g.cols[i]
	switch {
	case c.codes != nil:
		return c.codes[j] == 0
	case c.nums != nil:
		return math.IsNaN(c.nums[j])
	}
	return c.idx[j] == 0
}

// Wire is g as a CellGrid: every array against base nil (a keyframe),
// else the arrays that differ from base's, each dense or sparse (over the
// sentinel array in a keyframe, over base's in a delta), whichever is
// smaller. base, when set, has g's rect.
func (g *Grid) Wire(base *Grid) *mp.CellGrid {
	out := &mp.CellGrid{Rect: WireRect(g.Rect)}
	t := newTable(&out.Strings)
	index := func(k uint32) uint32 { return t.intern(g.str(k)) }
	for i, a := range arrays {
		col := g.cols[i]
		n := int(g.Rect.Width) * int(g.Rect.Height)
		var changed []int
		for j := 0; j < n; j++ {
			if base != nil && !g.same(base, i, j) || base == nil && !g.sentinel(i, j) {
				changed = append(changed, j)
			}
		}
		if base != nil && len(changed) == 0 {
			continue
		}
		sparse := &mp.SparseArray{}
		for _, j := range changed {
			sparse.Index = append(sparse.Index, uint32(j))
			switch a.kind {
			case kindNumber:
				sparse.Number = append(sparse.Number, col.nums[j])
			case kindCode:
				sparse.Code = append(sparse.Code, uint32(col.codes[j]))
			default:
				sparse.Code = append(sparse.Code, index(col.idx[j]))
			}
		}
		sparseArray := &mp.FieldArray{Form: &mp.FieldArray_Sparse{Sparse: sparse}}
		var dense *mp.FieldArray
		switch a.kind {
		case kindCode:
			dense = &mp.FieldArray{Form: &mp.FieldArray_Codes{Codes: slices.Clone(col.codes)}}
		case kindNumber:
			dense = &mp.FieldArray{Form: &mp.FieldArray_Numbers{Numbers: &mp.PackedDouble{Values: slices.Clone(col.nums)}}}
		default:
			idx := make([]uint32, n)
			for j, k := range col.idx {
				idx[j] = index(k)
			}
			dense = &mp.FieldArray{Form: &mp.FieldArray_Indexes{Indexes: &mp.PackedUint32{Values: idx}}}
		}
		if proto.Size(sparseArray) < proto.Size(dense) {
			a.put(out, sparseArray)
		} else {
			a.put(out, dense)
		}
	}
	out.Things = g.wireThings(base, func(s string) uint32 { return t.intern(s, true) })
	return out
}

// WireRect is r on the wire.
func WireRect(r policy.Rectangle) *mp.CellRect {
	return &mp.CellRect{X: proto.Int32(r.X), Z: proto.Int32(r.Z), Width: proto.Int32(r.Width), Height: proto.Int32(r.Height)}
}

func validRect(r *mp.CellRect) bool {
	return r != nil && r.X != nil && r.Z != nil && r.GetX() >= 0 && r.GetZ() >= 0 && r.GetWidth() >= 1 && r.GetHeight() >= 1 && int64(r.GetWidth())*int64(r.GetHeight()) <= MaxCells
}

// Complete reports whether g carries every array: a keyframe.
func Complete(g *mp.CellGrid) bool {
	for _, a := range arrays {
		if a.wire(g) == nil {
			return false
		}
	}
	return g.Things != nil
}

// Apply lays a wire grid over held: a keyframe (held ignored) must carry
// every array, a delta only the changed ones on held's rect. Sparse
// arrays apply over the sentinel array in a keyframe and over held's in a
// delta. held is left as it was.
func Apply(held *Grid, key bool, g *mp.CellGrid) (*Grid, error) {
	if g == nil || !validRect(g.Rect) {
		return nil, contract("mirror cell grid rect")
	}
	rect := policy.Rectangle{X: g.Rect.GetX(), Z: g.Rect.GetZ(), Width: g.Rect.GetWidth(), Height: g.Rect.GetHeight()}
	if !key && (held == nil || held.Rect != rect) {
		return nil, contract("mirror cell grid delta off the held rect")
	}
	n := int(rect.Width) * int(rect.Height)
	out := &Grid{Rect: rect, cols: make([]column, len(arrays))}
	if !key {
		out.strings = slices.Clone(held.strings)
	}
	t := newTable(&out.strings)
	// remap is a wire string index as an index into out's table.
	remap := func(k uint32) (uint32, error) {
		if int(k) > len(g.Strings) {
			return 0, contract("mirror cell grid string index out of range")
		}
		if k == 0 {
			return 0, nil
		}
		return t.intern(g.Strings[k-1], true), nil
	}
	for i, field := range arrays {
		wire := field.wire(g)
		var base *column
		if !key {
			base = &held.cols[i]
		}
		if wire == nil {
			switch {
			case !key:
				out.cols[i] = *base
			default:
				return nil, contract("mirror cell grid keyframe missing an array")
			}
			continue
		}
		col, err := decodeColumn(field.kind, wire, base, n, remap)
		if err != nil {
			return nil, err
		}
		out.cols[i] = col
	}
	var heldThings *thingStore
	if !key {
		heldThings = held.things
	}
	var err error
	if out.things, err = applyThings(heldThings, g.Things, n, g.Strings); err != nil {
		return nil, err
	}
	return out, nil
}

func decodeColumn(k kind, wire *mp.FieldArray, base *column, n int, remap func(uint32) (uint32, error)) (column, error) {
	var col column
	if base != nil {
		col = column{codes: slices.Clone(base.codes), nums: slices.Clone(base.nums), idx: slices.Clone(base.idx)}
	} else {
		col = newColumn(k, n)
	}
	set := func(j int, code uint32, num float64) error {
		switch k {
		case kindCode:
			if code > 2 {
				return contract("mirror cell grid code out of range")
			}
			col.codes[j] = uint8(code)
		case kindNumber:
			if math.IsInf(num, 0) {
				return contract("mirror cell grid number not finite")
			}
			col.nums[j] = num
		default:
			v, err := remap(code)
			if err != nil {
				return err
			}
			col.idx[j] = v
		}
		return nil
	}
	switch form := wire.Form.(type) {
	case *mp.FieldArray_Codes:
		if k != kindCode || len(form.Codes) != n {
			return column{}, contract("mirror cell grid codes array")
		}
		for j, b := range form.Codes {
			if err := set(j, uint32(b), 0); err != nil {
				return column{}, err
			}
		}
	case *mp.FieldArray_Numbers:
		if k != kindNumber || len(form.Numbers.GetValues()) != n {
			return column{}, contract("mirror cell grid numbers array")
		}
		for j, x := range form.Numbers.GetValues() {
			if err := set(j, 0, x); err != nil {
				return column{}, err
			}
		}
	case *mp.FieldArray_Indexes:
		if k != kindIndex || len(form.Indexes.GetValues()) != n {
			return column{}, contract("mirror cell grid indexes array")
		}
		for j, x := range form.Indexes.GetValues() {
			if err := set(j, x, 0); err != nil {
				return column{}, err
			}
		}
	case *mp.FieldArray_Sparse:
		s := form.Sparse
		values, other := len(s.GetCode()), len(s.GetNumber())
		if k == kindNumber {
			values, other = other, values
		}
		if values != len(s.GetIndex()) || other != 0 {
			return column{}, contract("mirror cell grid sparse array")
		}
		last := -1
		for p, at := range s.GetIndex() {
			if int(at) <= last || int(at) >= n {
				return column{}, contract("mirror cell grid sparse index")
			}
			last = int(at)
			var err error
			if k == kindNumber {
				err = set(int(at), 0, s.GetNumber()[p])
			} else {
				err = set(int(at), s.GetCode()[p], 0)
			}
			if err != nil {
				return column{}, err
			}
		}
	default:
		return column{}, contract("mirror cell grid array form")
	}
	return col, nil
}

func (g *Grid) cell(j int) policy.SiteCell {
	cell := policy.SiteCell{Cell: domain.Cell{X: g.Rect.X + int32(j%int(g.Rect.Width)), Z: g.Rect.Z + int32(j/int(g.Rect.Width))}}
	for i, a := range arrays[1:] {
		col := g.cols[i+1]
		switch a.kind {
		case kindCode:
			a.setCode(&cell, col.codes[j])
		case kindNumber:
			a.setNum(&cell, col.nums[j])
		default:
			s, known := g.str(col.idx[j])
			a.setStr(&cell, s, known)
		}
	}
	cell.Things = g.things.at(j)
	return cell
}

// Cells are the grid's held cells as site cells, row-major.
func (g *Grid) Cells() []policy.SiteCell {
	if g == nil {
		return nil
	}
	presence := g.cols[presenceArray].codes
	out := make([]policy.SiteCell, 0, len(presence))
	for j, code := range presence {
		if code == 1 {
			out = append(out, g.cell(j))
		}
	}
	return out
}

// Window is the held cells inside rect (clipped to the grid), row-major,
// and the count of rect's cells the grid does not hold (fogged). Glow is
// total light: a cell's glow, raised on an unroofed cell to sky (a frame's
// sky_glow; the grid's glow is artificial light only).
func (g *Grid) Window(rect policy.Rectangle, sky float64) ([]policy.SiteCell, uint64) {
	minX, minZ := max(rect.X, g.Rect.X), max(rect.Z, g.Rect.Z)
	maxX, maxZ := min(rect.X+rect.Width, g.Rect.X+g.Rect.Width), min(rect.Z+rect.Height, g.Rect.Z+g.Rect.Height)
	var out []policy.SiteCell
	var fogged uint64
	for z := minZ; z < maxZ; z++ {
		for x := minX; x < maxX; x++ {
			j := int(z-g.Rect.Z)*int(g.Rect.Width) + int(x-g.Rect.X)
			if g.cols[presenceArray].codes[j] != 1 {
				fogged++
				continue
			}
			cell := g.cell(j)
			if glow, known := cell.Glow.Value(); known && g.cols[roofedArray].codes[j] == 1 && sky > glow {
				cell.Glow = domain.Known(sky)
			}
			out = append(out, cell)
		}
	}
	return out, fogged
}
