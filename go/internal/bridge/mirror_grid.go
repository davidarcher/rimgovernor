package bridge

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

// The planning_cells mirror section travels as a CellGrid (#795,
// mirror.proto): one array per policy.SiteCell field over the window rect,
// row-major. CellGrid is a grid as the client holds it, every array whole
// and strings resolved, so a delta's arrays (whole, or sparse over the held
// array) apply without the native's string table.

type gridKind uint8

const (
	gridCode   gridKind = iota // presence and Fact[bool]: 0 unknown, 1 false, 2 true
	gridNumber                 // Fact[float64]: NaN unknown
	gridIndex                  // Fact[string]: 0 unknown, k strings[k-1]; ruin_hold 0 empty
)

// gridField is one CellGrid array: its kind, its accessor on the wire and
// how a cell's value lands on a SiteCell.
type gridField struct {
	kind gridKind
	wire func(*mp.CellGrid) *mp.FieldArray
	set  func(*policy.SiteCell, gridValue)
}

// gridValue is one cell of one array.
type gridValue struct {
	code  uint8
	num   float64
	str   string
	known bool // gridIndex: the string is known (index > 0)
}

func boolFact(v gridValue) domain.Fact[bool] {
	if v.code == 0 {
		return domain.Unknown[bool]()
	}
	return domain.Known(v.code == 2)
}

func numFact(v gridValue) domain.Fact[float64] {
	if math.IsNaN(v.num) {
		return domain.Unknown[float64]()
	}
	return domain.Known(v.num)
}

func strFact(v gridValue) domain.Fact[string] {
	if !v.known {
		return domain.Unknown[string]()
	}
	return domain.Known(v.str)
}

// gridFields are CellGrid's arrays in field order; the first is presence.
var gridFields = []gridField{
	{gridCode, (*mp.CellGrid).GetCell, nil},
	{gridCode, (*mp.CellGrid).GetWalkable, func(c *policy.SiteCell, v gridValue) { c.Walkable = boolFact(v) }},
	{gridCode, (*mp.CellGrid).GetOccupied, func(c *policy.SiteCell, v gridValue) { c.Occupied = boolFact(v) }},
	{gridCode, (*mp.CellGrid).GetZone, func(c *policy.SiteCell, v gridValue) { c.Zone = boolFact(v) }},
	{gridCode, (*mp.CellGrid).GetRoofed, func(c *policy.SiteCell, v gridValue) { c.Roofed = boolFact(v) }},
	{gridCode, (*mp.CellGrid).GetIndoors, func(c *policy.SiteCell, v gridValue) { c.Indoors = boolFact(v) }},
	{gridCode, (*mp.CellGrid).GetSupportsLight, func(c *policy.SiteCell, v gridValue) { c.SupportsLight = boolFact(v) }},
	{gridCode, (*mp.CellGrid).GetStorageEmpty, func(c *policy.SiteCell, v gridValue) { c.StorageEmpty = boolFact(v) }},
	{gridCode, (*mp.CellGrid).GetDoorway, func(c *policy.SiteCell, v gridValue) { c.Doorway = boolFact(v) }},
	{gridNumber, (*mp.CellGrid).GetFertility, func(c *policy.SiteCell, v gridValue) { c.Fertility = numFact(v) }},
	{gridCode, (*mp.CellGrid).GetPolluted, func(c *policy.SiteCell, v gridValue) { c.Polluted = boolFact(v) }},
	{gridNumber, (*mp.CellGrid).GetGlow, func(c *policy.SiteCell, v gridValue) { c.Glow = numFact(v) }},
	{gridIndex, (*mp.CellGrid).GetRoof, func(c *policy.SiteCell, v gridValue) { c.Roof = strFact(v) }},
	{gridIndex, (*mp.CellGrid).GetZoneId, func(c *policy.SiteCell, v gridValue) { c.ZoneID = strFact(v) }},
	{gridCode, (*mp.CellGrid).GetNaturalRock, func(c *policy.SiteCell, v gridValue) { c.NaturalRock = boolFact(v) }},
	{gridCode, (*mp.CellGrid).GetRuin, func(c *policy.SiteCell, v gridValue) { c.Ruin = boolFact(v) }},
	{gridIndex, (*mp.CellGrid).GetPlayerEdifice, func(c *policy.SiteCell, v gridValue) { c.PlayerEdifice = strFact(v) }},
	{gridIndex, (*mp.CellGrid).GetClaimableRuin, func(c *policy.SiteCell, v gridValue) { c.ClaimableRuin = strFact(v) }},
	{gridIndex, (*mp.CellGrid).GetRuinHold, func(c *policy.SiteCell, v gridValue) { c.RuinHold = v.str }},
}

// CellGrid is a planning window grid as held: the rect and every array.
type CellGrid struct {
	Rect policy.Rectangle
	cols [][]gridValue
}

// GridRect is a wire rect as a policy rectangle.
func GridRect(r *mp.CellRect) policy.Rectangle {
	return policy.Rectangle{X: r.GetX(), Z: r.GetZ(), Width: r.GetWidth(), Height: r.GetHeight()}
}

// WireRect is a policy rectangle as a wire rect.
func WireRect(r policy.Rectangle) *mp.CellRect {
	return &mp.CellRect{X: proto.Int32(r.X), Z: proto.Int32(r.Z), Width: proto.Int32(r.Width), Height: proto.Int32(r.Height)}
}

func validGridRect(r *mp.CellRect) bool {
	return r != nil && r.X != nil && r.Z != nil && r.GetX() >= 0 && r.GetZ() >= 0 && r.GetWidth() >= 1 && r.GetHeight() >= 1 && int64(r.GetWidth())*int64(r.GetHeight()) <= planningWindowPage
}

// ApplyCellGrid lays a wire grid over held: a keyframe (held ignored)
// must carry every array, a delta only the changed ones on held's rect.
// Sparse arrays apply over the sentinel array in a keyframe and over
// held's in a delta.
func ApplyCellGrid(held *CellGrid, key bool, g *mp.CellGrid) (*CellGrid, error) {
	if g == nil || !validGridRect(g.Rect) {
		return nil, contract("mirror cell grid rect")
	}
	rect := GridRect(g.Rect)
	if !key && (held == nil || held.Rect != rect) {
		return nil, contract("mirror cell grid delta off the held rect")
	}
	n := int(rect.Width) * int(rect.Height)
	out := &CellGrid{Rect: rect, cols: make([][]gridValue, len(gridFields))}
	for i, field := range gridFields {
		array := field.wire(g)
		var base []gridValue
		if !key {
			base = held.cols[i]
		}
		if array == nil {
			if key {
				return nil, contract("mirror cell grid keyframe missing an array")
			}
			out.cols[i] = base
			continue
		}
		col, err := gridColumn(field.kind, array, base, n, g.Strings)
		if err != nil {
			return nil, err
		}
		out.cols[i] = col
	}
	return out, nil
}

func gridColumn(kind gridKind, array *mp.FieldArray, base []gridValue, n int, strings []string) ([]gridValue, error) {
	value := func(code uint32, num float64) (gridValue, error) {
		switch kind {
		case gridCode:
			if code > 2 {
				return gridValue{}, contract("mirror cell grid code out of range")
			}
			return gridValue{code: uint8(code)}, nil
		case gridNumber:
			if math.IsInf(num, 0) {
				return gridValue{}, contract("mirror cell grid number not finite")
			}
			return gridValue{num: num}, nil
		}
		if int(code) > len(strings) {
			return gridValue{}, contract("mirror cell grid string index out of range")
		}
		if code == 0 {
			return gridValue{}, nil
		}
		return gridValue{str: strings[code-1], known: true}, nil
	}
	col := make([]gridValue, n)
	switch form := array.Form.(type) {
	case *mp.FieldArray_Codes:
		if kind != gridCode || len(form.Codes) != n {
			return nil, contract("mirror cell grid codes array")
		}
		for j, b := range form.Codes {
			v, err := value(uint32(b), 0)
			if err != nil {
				return nil, err
			}
			col[j] = v
		}
	case *mp.FieldArray_Numbers:
		if kind != gridNumber || len(form.Numbers.GetValues()) != n {
			return nil, contract("mirror cell grid numbers array")
		}
		for j, x := range form.Numbers.GetValues() {
			v, err := value(0, x)
			if err != nil {
				return nil, err
			}
			col[j] = v
		}
	case *mp.FieldArray_Indexes:
		if kind != gridIndex || len(form.Indexes.GetValues()) != n {
			return nil, contract("mirror cell grid indexes array")
		}
		for j, k := range form.Indexes.GetValues() {
			v, err := value(k, 0)
			if err != nil {
				return nil, err
			}
			col[j] = v
		}
	case *mp.FieldArray_Sparse:
		s := form.Sparse
		values, other := len(s.GetCode()), len(s.GetNumber())
		if kind == gridNumber {
			values, other = other, values
		}
		if values != len(s.GetIndex()) || other != 0 {
			return nil, contract("mirror cell grid sparse array")
		}
		if base != nil {
			copy(col, base)
		} else if kind == gridNumber {
			for j := range col {
				col[j].num = math.NaN()
			}
		}
		last := -1
		for p, at := range s.GetIndex() {
			if int(at) <= last || int(at) >= n {
				return nil, contract("mirror cell grid sparse index")
			}
			last = int(at)
			var v gridValue
			var err error
			if kind == gridNumber {
				v, err = value(0, s.GetNumber()[p])
			} else {
				v, err = value(s.GetCode()[p], 0)
			}
			if err != nil {
				return nil, err
			}
			col[at] = v
		}
	default:
		return nil, contract("mirror cell grid array form")
	}
	return col, nil
}

// GridArrays counts the arrays a wire grid carries.
func GridArrays(g *mp.CellGrid) int {
	n := 0
	for _, field := range gridFields {
		if g != nil && field.wire(g) != nil {
			n++
		}
	}
	return n
}

// Cells are the grid's held cells as site cells, row-major.
func (g *CellGrid) Cells() []policy.SiteCell {
	if g == nil {
		return nil
	}
	out := make([]policy.SiteCell, 0, len(g.cols[0]))
	for j, present := range g.cols[0] {
		if present.code != 1 {
			continue
		}
		cell := policy.SiteCell{Cell: domain.Cell{X: g.Rect.X + int32(j%int(g.Rect.Width)), Z: g.Rect.Z + int32(j/int(g.Rect.Width))}}
		for i, field := range gridFields[1:] {
			field.set(&cell, g.cols[i+1][j])
		}
		out = append(out, cell)
	}
	return out
}
