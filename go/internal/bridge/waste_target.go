package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// WasteTarget refreshes one exact waste item's CAS snapshot token. Unlike
// buildings (ReadConstructionBuildings/ListBuildings), a waste item has no
// exact-ID lookup RPC: the generic per-tick WasteReply census this package's
// ReadColonyFacts already carries does not itself refresh a usable token, so
// this scans the one cell the caller already knows the item occupies (the
// EntityRef.position the census carries for each row, mirroring Clean's Cell
// field for filth) via the generic observations_get_cells RPC and matches by
// entity ID within that cell's things -- the same pattern
// ReadFilthTarget uses.
type WasteTarget struct {
	Context *c.ObservationContext
	Item    string
	Token   string
}

func wasteTargetFields() *o.CellFields {
	return &o.CellFields{Terrain: proto.Bool(false), Roof: proto.Bool(false), Visibility: proto.Bool(false), Traversal: proto.Bool(false), Zone: proto.Bool(false), Areas: proto.Bool(false), Things: proto.Bool(true), Designations: proto.Bool(false), Room: proto.Bool(false), Growth: proto.Bool(false)}
}

// ReadWasteTarget observes one exact cell's things and matches the requested
// waste item by entity ID, refreshing its CAS snapshot token.
func (client *Client) ReadWasteTarget(ctx context.Context, identity *c.Identity, item string, cell domain.Cell) (WasteTarget, Result, error) {
	if validID(item) != nil || cell.X < 0 || cell.Z < 0 {
		return WasteTarget{}, Result{}, contract("invalid waste target identity")
	}
	point := &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}
	request := &o.GetCellsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Selection: &o.GetCellsRequest_ExactCells{ExactCells: &o.CellSelection{Cells: []*c.Cell{point}}}, Fields: wasteTargetFields(), Page: &c.PageRequest{Limit: proto.Uint32(1)}}
	reply := &o.GetCellsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_get_cells", request, reply)
	if err != nil {
		return WasteTarget{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.GetCellsReply_Failure:
		return WasteTarget{}, raw, failure(v.Failure, raw)
	case *o.GetCellsReply_Unavailable:
		return WasteTarget{}, raw, unavailable(v.Unavailable, raw)
	case *o.GetCellsReply_Observed:
		target, err := validateWasteTarget(v.Observed, request.Scope.ExpectedIdentity, point, item)
		return target, raw, err
	default:
		return WasteTarget{}, raw, contract("missing waste target cells outcome")
	}
}

func validateWasteTarget(snapshot *o.CellsSnapshot, identity *c.Identity, cell *c.Cell, item string) (WasteTarget, error) {
	if snapshot == nil || ValidateContext(snapshot.Context) != nil || !sameIdentity(snapshot.Context.Identity, identity) {
		return WasteTarget{}, contract("invalid waste target cells context")
	}
	completeness := snapshot.Completeness
	if completeness == nil || completeness.Page == nil || completeness.Page.Complete == nil || !completeness.Page.GetComplete() || completeness.Page.GetNextCursor() != "" || completeness.Matched == nil || completeness.GetMatched() != 1 || completeness.Returned == nil || completeness.GetReturned() != 1 || completeness.Filtered == nil || completeness.GetFiltered() != 0 || completeness.Unreadable == nil || completeness.GetUnreadable() != 0 {
		return WasteTarget{}, contract("incomplete waste target cells observation")
	}
	if !proto.Equal(snapshot.AppliedFields, wasteTargetFields()) {
		return WasteTarget{}, contract("waste target cells applied fields differ")
	}
	if len(snapshot.Cells) != 1 || snapshot.Cells[0] == nil || !proto.Equal(snapshot.Cells[0].Cell, cell) {
		return WasteTarget{}, contract("waste target cell differs")
	}
	things := snapshot.Cells[0].Things
	if len(things) > 256 {
		return WasteTarget{}, contract("waste target cell exceeds thing bound")
	}
	var found *o.CellThing
	for _, thing := range things {
		if thing == nil || thing.Thing == nil {
			return WasteTarget{}, contract("invalid cell thing")
		}
		if thing.Thing.GetId() == item {
			if found != nil {
				return WasteTarget{}, contract("ambiguous waste target entity")
			}
			found = thing
		}
	}
	if found == nil {
		return WasteTarget{}, contract("waste target missing")
	}
	if err := pawnsEntity(found.Thing, snapshot.Context); err != nil {
		return WasteTarget{}, err
	}
	if found.Thing.Snapshot == nil {
		return WasteTarget{}, contract("waste target CAS token unavailable")
	}
	return WasteTarget{Context: proto.Clone(snapshot.Context).(*c.ObservationContext), Item: item, Token: found.Thing.Snapshot.GetToken()}, nil
}
