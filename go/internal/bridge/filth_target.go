package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// FilthTarget refreshes one exact filth entity's CAS snapshot token. Unlike
// buildings (ReadConstructionBuildings/ListBuildings), filth has no exact-ID
// lookup RPC, so this scans the one cell the caller already knows the filth
// occupies (carried on domain.Clean, mirroring Haul's Cell field for loose
// things) via the generic observations_get_cells RPC and matches by entity
// ID within that cell's things.
type FilthTarget struct {
	Context *c.ObservationContext
	Filth   string
	Token   string
}

func filthFields() *o.CellFields {
	return &o.CellFields{Terrain: proto.Bool(false), Roof: proto.Bool(false), Visibility: proto.Bool(false), Traversal: proto.Bool(false), Zone: proto.Bool(false), Areas: proto.Bool(false), Things: proto.Bool(true), Designations: proto.Bool(false), Room: proto.Bool(false), Growth: proto.Bool(false)}
}

// ReadFilthTarget observes one exact cell's things and matches the requested
// filth by entity ID, refreshing its CAS snapshot token.
func (client *Client) ReadFilthTarget(ctx context.Context, identity *c.Identity, filth string, cell domain.Cell) (FilthTarget, Result, error) {
	if validID(filth) != nil || cell.X < 0 || cell.Z < 0 {
		return FilthTarget{}, Result{}, contract("invalid filth target identity")
	}
	point := &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}
	request := &o.GetCellsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Selection: &o.GetCellsRequest_ExactCells{ExactCells: &o.CellSelection{Cells: []*c.Cell{point}}}, Fields: filthFields(), Page: &c.PageRequest{Limit: proto.Uint32(1)}}
	reply := &o.GetCellsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_get_cells", request, reply)
	if err != nil {
		return FilthTarget{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.GetCellsReply_Failure:
		return FilthTarget{}, raw, failure(v.Failure, raw)
	case *o.GetCellsReply_Unavailable:
		return FilthTarget{}, raw, unavailable(v.Unavailable, raw)
	case *o.GetCellsReply_Observed:
		target, err := validateFilthTarget(v.Observed, request.Scope.ExpectedIdentity, point, filth)
		return target, raw, err
	default:
		return FilthTarget{}, raw, contract("missing filth cells outcome")
	}
}

func validateFilthTarget(snapshot *o.CellsSnapshot, identity *c.Identity, cell *c.Cell, filth string) (FilthTarget, error) {
	if snapshot == nil || ValidateContext(snapshot.Context) != nil || !sameIdentity(snapshot.Context.Identity, identity) {
		return FilthTarget{}, contract("invalid filth cells context")
	}
	completeness := snapshot.Completeness
	if completeness == nil || completeness.Page == nil || completeness.Page.Complete == nil || !completeness.Page.GetComplete() || completeness.Page.GetNextCursor() != "" || completeness.Matched == nil || completeness.GetMatched() != 1 || completeness.Returned == nil || completeness.GetReturned() != 1 || completeness.Filtered == nil || completeness.GetFiltered() != 0 || completeness.Unreadable == nil || completeness.GetUnreadable() != 0 {
		return FilthTarget{}, contract("incomplete filth cells observation")
	}
	if !proto.Equal(snapshot.AppliedFields, filthFields()) {
		return FilthTarget{}, contract("filth cells applied fields differ")
	}
	if len(snapshot.Cells) != 1 || snapshot.Cells[0] == nil || !proto.Equal(snapshot.Cells[0].Cell, cell) {
		return FilthTarget{}, contract("filth cell differs")
	}
	things := snapshot.Cells[0].Things
	if len(things) > 256 {
		return FilthTarget{}, contract("filth cell exceeds thing bound")
	}
	var found *o.CellThing
	for _, thing := range things {
		if thing == nil || thing.Thing == nil {
			return FilthTarget{}, contract("invalid cell thing")
		}
		if thing.Thing.GetId() == filth {
			if found != nil {
				return FilthTarget{}, contract("ambiguous filth entity")
			}
			found = thing
		}
	}
	if found == nil {
		return FilthTarget{}, contract("filth target missing")
	}
	if err := pawnsEntity(found.Thing, snapshot.Context); err != nil {
		return FilthTarget{}, err
	}
	if found.Thing.Snapshot == nil {
		return FilthTarget{}, contract("filth target CAS token unavailable")
	}
	return FilthTarget{Context: proto.Clone(snapshot.Context).(*c.ObservationContext), Filth: filth, Token: found.Thing.Snapshot.GetToken()}, nil
}
