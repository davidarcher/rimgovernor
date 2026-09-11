package bridge

import (
	"context"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// MapBounds preserves the native context of known map dimensions. It does not
// certify placement safety, resources or permission to write.
type MapBounds struct {
	Context *c.ObservationContext
	Bounds  policy.Bounds
}

// ReadMapBounds reads one caller-observed anchor with all cell details disabled.
// A failed, incomplete or mismatched observation never supplies default bounds.
func (client *Client) ReadMapBounds(ctx context.Context, identity *c.Identity, anchor domain.Cell) (MapBounds, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return MapBounds{}, Result{}, err
	}
	if anchor.X < 0 || anchor.Z < 0 {
		return MapBounds{}, Result{}, contract("negative bounds anchor")
	}
	cell := &c.Cell{X: proto.Int32(anchor.X), Z: proto.Int32(anchor.Z)}
	request := &o.GetCellsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Selection: &o.GetCellsRequest_ExactCells{ExactCells: &o.CellSelection{Cells: []*c.Cell{cell}}}, Fields: mapBoundsFields(), Page: &c.PageRequest{Limit: proto.Uint32(1)}}
	reply := &o.GetCellsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_get_cells", request, reply)
	if err != nil {
		return MapBounds{}, raw, err
	}
	switch value := reply.Outcome.(type) {
	case *o.GetCellsReply_Failure:
		return MapBounds{}, raw, failure(value.Failure, raw)
	case *o.GetCellsReply_Unavailable:
		return MapBounds{}, raw, unavailable(value.Unavailable, raw)
	case *o.GetCellsReply_Observed:
		bounds, err := validateMapBounds(value.Observed, request.Scope.ExpectedIdentity, cell)
		return bounds, raw, err
	default:
		return MapBounds{}, raw, contract("missing map bounds outcome")
	}
}
func mapBoundsFields() *o.CellFields {
	return &o.CellFields{Terrain: proto.Bool(false), Roof: proto.Bool(false), Visibility: proto.Bool(false), Traversal: proto.Bool(false), Zone: proto.Bool(false), Areas: proto.Bool(false), Things: proto.Bool(false), Designations: proto.Bool(false), Room: proto.Bool(false), Growth: proto.Bool(false)}
}
func validateMapBounds(snapshot *o.CellsSnapshot, identity *c.Identity, anchor *c.Cell) (MapBounds, error) {
	if snapshot == nil {
		return MapBounds{}, contract("missing cells snapshot")
	}
	if err := ValidateContext(snapshot.Context); err != nil {
		return MapBounds{}, err
	}
	if !sameIdentity(snapshot.Context.Identity, identity) {
		return MapBounds{}, contract("map bounds identity mismatch")
	}
	size := snapshot.MapSize
	if size == nil || size.Width == nil || size.Height == nil || size.GetWidth() == 0 || size.GetHeight() == 0 || size.GetWidth() > math.MaxInt32 || size.GetHeight() > math.MaxInt32 {
		return MapBounds{}, contract("invalid map dimensions")
	}
	bounds := policy.Bounds{Width: int32(size.GetWidth()), Height: int32(size.GetHeight())}
	inside := func(cell *c.Cell) bool {
		return cell != nil && cell.X != nil && cell.Z != nil && cell.GetX() >= 0 && cell.GetZ() >= 0 && cell.GetX() < bounds.Width && cell.GetZ() < bounds.Height
	}
	if !inside(anchor) {
		return MapBounds{}, contract("bounds exclude requested anchor")
	}
	if region := snapshot.Region; region != nil {
		if !inside(region.Minimum) || !inside(region.Maximum) || region.Minimum.GetX() > anchor.GetX() || region.Minimum.GetZ() > anchor.GetZ() || region.Maximum.GetX() < anchor.GetX() || region.Maximum.GetZ() < anchor.GetZ() {
			return MapBounds{}, contract("invalid bounds region")
		}
	}
	completeness := snapshot.Completeness
	if completeness == nil || completeness.Page == nil || completeness.Page.Complete == nil || !completeness.Page.GetComplete() || completeness.Page.GetNextCursor() != "" || completeness.Matched == nil || completeness.GetMatched() != 1 || completeness.Returned == nil || completeness.GetReturned() != 1 || completeness.Filtered == nil || completeness.GetFiltered() != 0 || completeness.Unreadable == nil || completeness.GetUnreadable() != 0 {
		return MapBounds{}, contract("incomplete map bounds observation")
	}
	if completeness.SnapshotToken != nil {
		if err := validID(completeness.GetSnapshotToken()); err != nil {
			return MapBounds{}, err
		}
	}
	if !proto.Equal(snapshot.AppliedFields, mapBoundsFields()) {
		return MapBounds{}, contract("map bounds applied fields differ")
	}
	// The exact minimal row also rejects unrequested facts and read issues. No
	// arbitrary native detail is silently accepted by this narrow projection.
	if len(snapshot.Cells) != 1 || !proto.Equal(snapshot.Cells[0], &o.CellState{Cell: anchor}) {
		return MapBounds{}, contract("map bounds cell differs or contains unrequested facts")
	}
	if ref := snapshot.MapSnapshot; ref != nil {
		if err := ValidateContext(ref.Context); err != nil {
			return MapBounds{}, err
		}
		if !proto.Equal(ref.Context, snapshot.Context) {
			return MapBounds{}, contract("map snapshot context mismatch")
		}
		if err := validID(ref.GetEntityId()); err != nil {
			return MapBounds{}, err
		}
		if err := validID(ref.GetToken()); err != nil {
			return MapBounds{}, err
		}
	}
	return MapBounds{Context: proto.Clone(snapshot.Context).(*c.ObservationContext), Bounds: bounds}, nil
}
