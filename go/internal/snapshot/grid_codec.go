package snapshot

import (
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

// The grid line's decoder (grid.go holds the encoder). The recorder is the
// CellGrid format's only reader since the frame stream replaced mirror_poll
// (#858); both halves share gridArrays.

func contract(msg string) error { return fmt.Errorf("snapshot: %s", msg) }

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

// cellGrid is a grid as replay holds it: the rect and every array.
type cellGrid struct {
	Rect policy.Rectangle
	cols [][]gridValue
}

func wireRect(r policy.Rectangle) *mp.CellRect {
	return &mp.CellRect{X: proto.Int32(r.X), Z: proto.Int32(r.Z), Width: proto.Int32(r.Width), Height: proto.Int32(r.Height)}
}

func validGridRect(r *mp.CellRect) bool {
	return r != nil && r.X != nil && r.Z != nil && r.GetX() >= 0 && r.GetZ() >= 0 && r.GetWidth() >= 1 && r.GetHeight() >= 1 && int64(r.GetWidth())*int64(r.GetHeight()) <= 1<<16
}

// applyCellGrid lays a wire grid over held: a keyframe (held ignored)
// must carry every array, a delta only the changed ones on held's rect.
// Sparse arrays apply over the sentinel array in a keyframe and over
// held's in a delta.
func applyCellGrid(held *cellGrid, key bool, g *mp.CellGrid) (*cellGrid, error) {
	if g == nil || !validGridRect(g.Rect) {
		return nil, contract("mirror cell grid rect")
	}
	rect := policy.Rectangle{X: g.Rect.GetX(), Z: g.Rect.GetZ(), Width: g.Rect.GetWidth(), Height: g.Rect.GetHeight()}
	if !key && (held == nil || held.Rect != rect) {
		return nil, contract("mirror cell grid delta off the held rect")
	}
	n := int(rect.Width) * int(rect.Height)
	out := &cellGrid{Rect: rect, cols: make([][]gridValue, len(gridArrays))}
	for i, field := range gridArrays {
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

// Cells are the grid's held cells as site cells, row-major.
func (g *cellGrid) Cells() []policy.SiteCell {
	if g == nil {
		return nil
	}
	out := make([]policy.SiteCell, 0, len(g.cols[0]))
	for j, present := range g.cols[0] {
		if present.code != 1 {
			continue
		}
		cell := policy.SiteCell{Cell: domain.Cell{X: g.Rect.X + int32(j%int(g.Rect.Width)), Z: g.Rect.Z + int32(j/int(g.Rect.Width))}}
		for i, field := range gridArrays[1:] {
			field.set(&cell, g.cols[i+1][j])
		}
		out = append(out, cell)
	}
	return out
}
