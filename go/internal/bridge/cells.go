package bridge

import (
	"context"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/cellgrid"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// cellsRead is one validated observations_get_cells read (#1346): the
// map's bounds and the rect as a decoded grid. The grid carries each cell's
// things, terrain and foundation affordances (#2260, #2272).
type cellsRead struct {
	Context *c.ObservationContext
	Bounds  policy.Bounds
	Grid    *cellgrid.Grid
}

// readCells reads rect through observations_get_cells and validates the
// reply against the request.
func (client *Client) readCells(ctx context.Context, identity *c.Identity, rect policy.Rectangle) (cellsRead, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return cellsRead{}, Result{}, err
	}
	if rect.X < 0 || rect.Z < 0 || rect.Width < 1 || rect.Height < 1 || int64(rect.Width)*int64(rect.Height) > cellgrid.MaxCells {
		return cellsRead{}, Result{}, contract("invalid cells rect")
	}
	request := &o.GetCellsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)},
		Rectangle: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(rect.X), Z: proto.Int32(rect.Z)}, Maximum: &c.Cell{X: proto.Int32(rect.X + rect.Width - 1), Z: proto.Int32(rect.Z + rect.Height - 1)}}}
	reply := &o.GetCellsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_get_cells", request, reply)
	if err != nil {
		return cellsRead{}, raw, err
	}
	switch value := reply.Outcome.(type) {
	case *o.GetCellsReply_Failure:
		return cellsRead{}, raw, failure(value.Failure, raw)
	case *o.GetCellsReply_Unavailable:
		return cellsRead{}, raw, unavailable(value.Unavailable, raw)
	case *o.GetCellsReply_Observed:
		read, err := validateCells(value.Observed, identity, rect)
		return read, raw, err
	default:
		return cellsRead{}, raw, contract("missing cells outcome")
	}
}

// validateCells checks a cells snapshot answers the request: its world,
// a map holding rect and a keyframe grid over exactly rect.
func validateCells(v *o.CellsSnapshot, identity *c.Identity, rect policy.Rectangle) (cellsRead, error) {
	if v == nil {
		return cellsRead{}, contract("missing cells snapshot")
	}
	if err := ValidateContext(v.Context); err != nil {
		return cellsRead{}, err
	}
	if !sameIdentity(v.Context.Identity, identity) {
		return cellsRead{}, contract("cells identity mismatch")
	}
	size := v.MapSize
	if size == nil || size.Width == nil || size.Height == nil || size.GetWidth() == 0 || size.GetHeight() == 0 || size.GetWidth() > math.MaxInt32 || size.GetHeight() > math.MaxInt32 {
		return cellsRead{}, contract("invalid map dimensions")
	}
	bounds := policy.Bounds{Width: int32(size.GetWidth()), Height: int32(size.GetHeight())}
	if rect.X+rect.Width > bounds.Width || rect.Z+rect.Height > bounds.Height {
		return cellsRead{}, contract("cells rect off the map")
	}
	if v.Grid == nil || !cellgrid.Complete(v.Grid) {
		return cellsRead{}, contract("cells grid is not a keyframe")
	}
	grid, err := cellgrid.Apply(nil, true, v.Grid)
	if err != nil {
		return cellsRead{}, err
	}
	if grid.Rect != rect {
		return cellsRead{}, contract("cells grid rect differs")
	}
	return cellsRead{Context: v.Context, Bounds: bounds, Grid: grid}, nil
}

// RectCells are an inclusive wire rectangle's cells, row-major; nil for a
// rectangle missing a corner coordinate or inverted.
func RectCells(r *o.Rectangle) []domain.Cell {
	lo, hi := r.GetMinimum(), r.GetMaximum()
	if lo == nil || hi == nil || lo.X == nil || lo.Z == nil || hi.X == nil || hi.Z == nil || hi.GetX() < lo.GetX() || hi.GetZ() < lo.GetZ() {
		return nil
	}
	out := make([]domain.Cell, 0, int(hi.GetX()-lo.GetX()+1)*int(hi.GetZ()-lo.GetZ()+1))
	for z := lo.GetZ(); z <= hi.GetZ(); z++ {
		for x := lo.GetX(); x <= hi.GetX(); x++ {
			out = append(out, domain.Cell{X: x, Z: z})
		}
	}
	return out
}

// WireRect is the inclusive wire rectangle bounding cells; nil for none.
func WireRect(cells []domain.Cell) *o.Rectangle {
	if len(cells) == 0 {
		return nil
	}
	lo, hi := cells[0], cells[0]
	for _, cell := range cells[1:] {
		lo.X, lo.Z, hi.X, hi.Z = min(lo.X, cell.X), min(lo.Z, cell.Z), max(hi.X, cell.X), max(hi.Z, cell.Z)
	}
	return &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(lo.X), Z: proto.Int32(lo.Z)}, Maximum: &c.Cell{X: proto.Int32(hi.X), Z: proto.Int32(hi.Z)}}
}
