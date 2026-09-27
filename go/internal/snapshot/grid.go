package snapshot

import (
	"encoding/json"
	"errors"
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

// Planning cells as a grid (#795). A planning_cells section line holds the
// window as the wire's CellGrid (mirror.proto) rather than a row per cell:
//
//	{"Section": {"Name": "planning_cells", ..., "Key": true, "Grid": "<base64 proto>"}}
//
// over the rows' bounding rect, one array per policy.SiteCell field with
// the wire's sentinels (cell 0 not held, 1 held; bools 0 unknown, 1 false,
// 2 true; floats NaN unknown; strings 0 unknown, k for strings[k-1]). A
// keyframe carries every array; a delta, on the held rect, only the arrays
// that changed, each dense or sparse over the held array, whichever is
// smaller. Replay applies it with applyCellGrid and rebuilds the
// rows as Encode's JSON of its cells. The writer checks that rebuild
// against the rows it was given and falls back to the row form (Upserts,
// Removed, which older streams hold for planning_cells) when they differ,
// so a grid line always rebuilds its rows byte for byte.

type gridKind uint8

const (
	gridCode   gridKind = iota // presence and Fact[bool]
	gridNumber                 // Fact[float64]
	gridIndex                  // Fact[string], and ruin_hold
)

// gridValue is one cell of one array, line-independent: a code, a number
// (NaN unknown) or a string (known false: unknown, or ruin_hold empty).
type gridValue struct {
	code  uint8
	num   float64
	str   string
	known bool
}

func (v gridValue) same(o gridValue) bool {
	return v.code == o.code && math.Float64bits(v.num) == math.Float64bits(o.num) && v.str == o.str && v.known == o.known
}

var gridSentinel = [...]gridValue{gridCode: {}, gridNumber: {num: math.NaN()}, gridIndex: {}}

func boolValue(f domain.Fact[bool]) gridValue {
	switch b, known := f.Value(); {
	case !known:
		return gridValue{}
	case b:
		return gridValue{code: 2}
	}
	return gridValue{code: 1}
}

func numValue(f domain.Fact[float64]) gridValue {
	if x, known := f.Value(); known {
		return gridValue{num: x}
	}
	return gridValue{num: math.NaN()}
}

func strValue(f domain.Fact[string]) gridValue {
	s, known := f.Value()
	return gridValue{str: s, known: known}
}

// gridArray is one CellGrid array, in the wire's field order (presence
// first): its kind, its slot on the wire, and a SiteCell's value read
// (get, the encoder) and written (set, the decoder).
type gridArray struct {
	kind gridKind
	slot func(*mp.CellGrid) **mp.FieldArray
	get  func(*policy.SiteCell) gridValue
	set  func(*policy.SiteCell, gridValue)
}

func (a gridArray) wire(g *mp.CellGrid) *mp.FieldArray   { return *a.slot(g) }
func (a gridArray) put(g *mp.CellGrid, f *mp.FieldArray) { *a.slot(g) = f }

func boolArray(slot func(*mp.CellGrid) **mp.FieldArray, field func(*policy.SiteCell) *domain.Fact[bool]) gridArray {
	return gridArray{gridCode, slot, func(c *policy.SiteCell) gridValue { return boolValue(*field(c)) }, func(c *policy.SiteCell, v gridValue) { *field(c) = boolFact(v) }}
}

func numArray(slot func(*mp.CellGrid) **mp.FieldArray, field func(*policy.SiteCell) *domain.Fact[float64]) gridArray {
	return gridArray{gridNumber, slot, func(c *policy.SiteCell) gridValue { return numValue(*field(c)) }, func(c *policy.SiteCell, v gridValue) { *field(c) = numFact(v) }}
}

func strArray(slot func(*mp.CellGrid) **mp.FieldArray, field func(*policy.SiteCell) *domain.Fact[string]) gridArray {
	return gridArray{gridIndex, slot, func(c *policy.SiteCell) gridValue { return strValue(*field(c)) }, func(c *policy.SiteCell, v gridValue) { *field(c) = strFact(v) }}
}

var gridArrays = []gridArray{
	{gridCode, func(g *mp.CellGrid) **mp.FieldArray { return &g.Cell }, func(*policy.SiteCell) gridValue { return gridValue{code: 1} }, nil},
	boolArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.Walkable }, func(c *policy.SiteCell) *domain.Fact[bool] { return &c.Walkable }),
	boolArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.Occupied }, func(c *policy.SiteCell) *domain.Fact[bool] { return &c.Occupied }),
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
	boolArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.NaturalRock }, func(c *policy.SiteCell) *domain.Fact[bool] { return &c.NaturalRock }),
	boolArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.Ruin }, func(c *policy.SiteCell) *domain.Fact[bool] { return &c.Ruin }),
	strArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.PlayerEdifice }, func(c *policy.SiteCell) *domain.Fact[string] { return &c.PlayerEdifice }),
	strArray(func(g *mp.CellGrid) **mp.FieldArray { return &g.ClaimableRuin }, func(c *policy.SiteCell) *domain.Fact[string] { return &c.ClaimableRuin }),
	{gridIndex, func(g *mp.CellGrid) **mp.FieldArray { return &g.RuinHold }, func(c *policy.SiteCell) gridValue { return gridValue{str: c.RuinHold, known: c.RuinHold != ""} }, func(c *policy.SiteCell, v gridValue) { c.RuinHold = v.str }},
}

// heldGrid is a planning window as the writer holds it: the rect, every
// array, and the grid as replay rebuilds it (the next delta's base).
type heldGrid struct {
	rect  policy.Rectangle
	cols  [][]gridValue
	built *cellGrid
}

var errNotGrid = errors.New("snapshot: rows are not a planning grid")

// newGridCols is rows (a planning section's map[domain.Cell]SiteCell) as
// arrays over their bounding rect.
func newGridCols(rows any) (policy.Rectangle, [][]gridValue, error) {
	cells, ok := rows.(map[domain.Cell]policy.SiteCell)
	if !ok || len(cells) == 0 {
		return policy.Rectangle{}, nil, errNotGrid
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
	if n > 1<<16 {
		return policy.Rectangle{}, nil, errNotGrid
	}
	cols := make([][]gridValue, len(gridArrays))
	for i, a := range gridArrays {
		cols[i] = make([]gridValue, n)
		for j := range cols[i] {
			cols[i][j] = gridSentinel[a.kind]
		}
	}
	for c, row := range cells {
		j := int(c.Z-rect.Z)*int(rect.Width) + int(c.X-rect.X)
		for i, a := range gridArrays {
			cols[i][j] = a.get(&row)
		}
	}
	return rect, cols, nil
}

// wireGrid is cols as a CellGrid: every array against base nil (a
// keyframe), else the arrays that differ from base's, each dense or sparse
// (over the sentinel array in a keyframe, over base's in a delta),
// whichever is smaller.
func wireGrid(rect policy.Rectangle, cols [][]gridValue, base [][]gridValue) *mp.CellGrid {
	g := &mp.CellGrid{Rect: wireRect(rect)}
	table := map[string]uint32{}
	index := func(v gridValue) uint32 {
		if !v.known {
			return 0
		}
		k, ok := table[v.str]
		if !ok {
			g.Strings = append(g.Strings, v.str)
			k = uint32(len(g.Strings))
			table[v.str] = k
		}
		return k
	}
	for i, a := range gridArrays {
		col := cols[i]
		var changed []int
		for j, v := range col {
			var was gridValue
			if base != nil {
				was = base[i][j]
			} else {
				was = gridSentinel[a.kind]
			}
			if !v.same(was) {
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
			case gridNumber:
				sparse.Number = append(sparse.Number, col[j].num)
			case gridCode:
				sparse.Code = append(sparse.Code, uint32(col[j].code))
			default:
				sparse.Code = append(sparse.Code, index(col[j]))
			}
		}
		sparseArray := &mp.FieldArray{Form: &mp.FieldArray_Sparse{Sparse: sparse}}
		var dense *mp.FieldArray
		switch a.kind {
		case gridCode:
			codes := make([]byte, len(col))
			for j, v := range col {
				codes[j] = v.code
			}
			dense = &mp.FieldArray{Form: &mp.FieldArray_Codes{Codes: codes}}
		case gridNumber:
			nums := make([]float64, len(col))
			for j, v := range col {
				nums[j] = v.num
			}
			dense = &mp.FieldArray{Form: &mp.FieldArray_Numbers{Numbers: &mp.PackedDouble{Values: nums}}}
		default:
			idx := make([]uint32, len(col))
			for j, v := range col {
				idx[j] = index(v)
			}
			dense = &mp.FieldArray{Form: &mp.FieldArray_Indexes{Indexes: &mp.PackedUint32{Values: idx}}}
		}
		if proto.Size(sparseArray) < proto.Size(dense) {
			a.put(g, sparseArray)
		} else {
			a.put(g, dense)
		}
	}
	return g
}

// gridRows applies a wire grid over held (nil for a keyframe) and is the
// rows it describes as the section holds them.
func gridRows(held *cellGrid, key bool, g *mp.CellGrid) (*cellGrid, map[string]json.RawMessage, map[string]json.RawMessage, error) {
	built, err := applyCellGrid(held, key, g)
	if err != nil {
		return nil, nil, nil, err
	}
	cells := built.Cells()
	rows := make(map[domain.Cell]policy.SiteCell, len(cells))
	for _, c := range cells {
		rows[c.Cell] = c
	}
	keys, out, err := encodeRows(rows)
	if err != nil {
		return nil, nil, nil, err
	}
	return built, keys, out, nil
}

// gridFrame is rows (encoded as keys and encoded) as a grid line over
// held (nil: a keyframe), with the grid the writer then holds; ok is false
// when the rows are not a grid or the grid would not rebuild them exactly.
func gridFrame(held *heldGrid, raw any, encoded map[string]json.RawMessage) ([]byte, *heldGrid, bool) {
	rect, cols, err := newGridCols(raw)
	if err != nil {
		return nil, nil, false
	}
	key := held == nil || held.rect != rect
	var base [][]gridValue
	var built *cellGrid
	if !key {
		base, built = held.cols, held.built
	}
	return encodeGrid(&heldGrid{rect: rect, cols: cols}, key, base, built, encoded)
}

// encodeGrid writes next over base (nil: a keyframe) and checks it
// rebuilds encoded.
func encodeGrid(next *heldGrid, key bool, base [][]gridValue, built *cellGrid, encoded map[string]json.RawMessage) ([]byte, *heldGrid, bool) {
	wire := wireGrid(next.rect, next.cols, base)
	rebuilt, _, rows, err := gridRows(built, key, wire)
	if err != nil || len(rows) != len(encoded) {
		return nil, nil, false
	}
	for k, v := range encoded {
		if string(rows[k]) != string(v) {
			return nil, nil, false
		}
	}
	data, err := proto.MarshalOptions{Deterministic: true}.Marshal(wire)
	if err != nil {
		return nil, nil, false
	}
	next.built = rebuilt
	return data, next, true
}

// applyGrid lays a grid line over s (a replay).
func (s *recSection) applyGrid(f sectionFrame) (*recSection, error) {
	var held *cellGrid
	if !f.Key {
		if s == nil || s.grid == nil {
			return nil, fmt.Errorf("snapshot: section %s grid delta without a held grid", f.Name)
		}
		held = s.grid.built
	}
	var g mp.CellGrid
	if err := proto.Unmarshal(f.Grid, &g); err != nil {
		return nil, fmt.Errorf("snapshot: section %s grid: %w", f.Name, err)
	}
	built, keys, rows, err := gridRows(held, f.Key, &g)
	if err != nil {
		return nil, fmt.Errorf("snapshot: section %s grid: %w", f.Name, err)
	}
	return &recSection{scope: f.Scope, version: f.Version, asOf: f.AsOf, keys: keys, rows: rows, grid: &heldGrid{built: built}}, nil
}
