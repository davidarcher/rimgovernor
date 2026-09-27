package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// ReadPackedItems lists the exact ids of the colony's spawned, unheld
// packed (minified) items of one packed definition (#830), for
// InstallBuilding on a packed piece.
func (client *Client) ReadPackedItems(ctx context.Context, identity *c.Identity, packedDef string) ([]string, Result, error) {
	if ValidateIdentity(identity) != nil || validID(packedDef) != nil {
		return nil, Result{}, contract("invalid packed items read")
	}
	request := &o.ListSuppliesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)},
		Filter: &o.StockFilter{DefNames: []string{packedDef}, Ownership: proto.String("ours"), IncludeHeld: proto.Bool(false)}}
	reply := &o.ListSuppliesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_supplies", request, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown packed items fields")
	}
	var snapshot *o.SuppliesSnapshot
	switch v := reply.Outcome.(type) {
	case *o.ListSuppliesReply_Observed:
		snapshot = v.Observed
	case *o.ListSuppliesReply_Unavailable:
		return nil, raw, unavailable(v.Unavailable, raw)
	case *o.ListSuppliesReply_Failure:
		return nil, raw, failure(v.Failure, raw)
	default:
		return nil, raw, contract("missing packed items outcome")
	}
	if snapshot == nil || ValidateContext(snapshot.Context) != nil || !sameIdentity(snapshot.Context.Identity, identity) {
		return nil, raw, contract("invalid packed items context")
	}
	var out []string
	for _, row := range snapshot.Stocks {
		if row == nil || row.Definition.GetDefName() != packedDef {
			return nil, raw, contract("invalid packed items row")
		}
		for _, item := range row.Items {
			if validID(item.GetId()) != nil {
				return nil, raw, contract("invalid packed item id")
			}
			out = append(out, item.GetId())
		}
	}
	return out, raw, nil
}

// ResolvePackedInstall previews InstallBuilding on one packed item and
// returns the inner building's id and definition the native projection
// names; the move then carries the inner id, which native resolves back to
// the packed item (#830).
func (client *Client) ResolvePackedInstall(ctx context.Context, identity *c.Identity, packed string, cell domain.Cell, rot domain.Rotation) (string, string, Result, error) {
	rotation, ok := moveRotations[rot]
	if ValidateIdentity(identity) != nil || validID(packed) != nil || !ok {
		return "", "", Result{}, contract("invalid packed install preview")
	}
	operation := &op.Operation{Command: &op.Operation_InstallBuilding{InstallBuilding: &op.InstallBuilding{
		PackedOrInner: &op.EntityPrecondition{EntityId: proto.String(packed)},
		Destination:   &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}, Rotation: rotation.Enum()}}}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: operation}, reply)
	if err != nil {
		return "", "", raw, err
	}
	if buildingUnknown(reply) != nil {
		return "", "", raw, contract("unknown packed install preview fields")
	}
	if reply.GetFailure() != nil {
		return "", "", raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	e := v.GetProjected().GetInstallation()
	if v == nil || !v.GetAccepted() || buildingContext(v.Context, identity, 0, false) != nil || e == nil || validID(e.GetInnerThingId()) != nil || validID(e.GetDefName()) != nil ||
		e.GetStage() != r.InstallationStage_INSTALLATION_STAGE_PLACEABLE || e.GetCell().GetX() != cell.X || e.GetCell().GetZ() != cell.Z || e.GetRotation() != rotation {
		return "", "", raw, contract("invalid packed install preview evidence")
	}
	return e.GetInnerThingId(), e.GetDefName(), raw, nil
}
