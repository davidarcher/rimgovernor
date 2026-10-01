package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ReadBareCells reads cells' things and reports which hold no plant (a
// thing with a growth value): unsown or harvested soil a shrinking growing
// zone may give up (#1309). A cell missing from the reply is not bare.
func (client *Client) ReadBareCells(ctx context.Context, identity *c.Identity, cells []domain.Cell) (map[domain.Cell]bool, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if len(cells) == 0 {
		return map[domain.Cell]bool{}, Result{}, nil
	}
	selection := &o.CellSelection{}
	for _, cell := range cells {
		selection.Cells = append(selection.Cells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
	}
	f := proto.Bool(false)
	fields := &o.CellFields{Terrain: f, Roof: f, Visibility: f, Traversal: f, Zone: f, Areas: f, Things: proto.Bool(true), Designations: f, Room: f, Growth: f}
	request := &o.GetCellsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Selection: &o.GetCellsRequest_ExactCells{ExactCells: selection}, Fields: fields}
	reply := &o.GetCellsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_get_cells", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch value := reply.Outcome.(type) {
	case *o.GetCellsReply_Failure:
		return nil, raw, failure(value.Failure, raw)
	case *o.GetCellsReply_Unavailable:
		return nil, raw, unavailable(value.Unavailable, raw)
	case *o.GetCellsReply_Observed:
		snapshot := value.Observed
		if snapshot == nil {
			return nil, raw, contract("missing cells snapshot")
		}
		if err := ValidateContext(snapshot.Context); err != nil {
			return nil, raw, err
		}
		if !sameIdentity(snapshot.Context.Identity, identity) {
			return nil, raw, contract("bare cells identity mismatch")
		}
		out := map[domain.Cell]bool{}
		for _, row := range snapshot.Cells {
			if row.GetCell() == nil || row.Cell.X == nil || row.Cell.Z == nil {
				continue
			}
			bare := true
			for _, thing := range row.Things {
				if thing.Growth != nil {
					bare = false
				}
			}
			if bare {
				out[domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()}] = true
			}
		}
		return out, raw, nil
	default:
		return nil, raw, contract("missing cells outcome")
	}
}
