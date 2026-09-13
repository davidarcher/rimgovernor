package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type HaulCandidate struct {
	Thing, Definition string
	Cell              domain.Cell
	Token             string
}
type HaulRead struct {
	Context *c.ObservationContext
	Targets []HaulCandidate
}

// ReadHaulTargets observes loose haulable items at one cell, forbidden or not:
// hauling a forbidden item is simply refused downstream by the native
// preview, not filtered out here.
func (client *Client) ReadHaulTargets(ctx context.Context, identity *c.Identity, cell domain.Cell) (HaulRead, Result, error) {
	if ValidateIdentity(identity) != nil || cell.X < 0 || cell.Z < 0 {
		return HaulRead{}, Result{}, contract("invalid haul scope")
	}
	point := &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}
	request := &o.ListSuppliesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Filter: &o.StockFilter{Category: proto.String("haulable"), Ownership: proto.String("ours"), IncludeHeld: proto.Bool(false), ForbiddenOnly: proto.Bool(false), Region: &o.Rectangle{Minimum: point, Maximum: proto.Clone(point).(*c.Cell)}}, Page: &c.PageRequest{Limit: proto.Uint32(256)}}
	reply := &o.ListSuppliesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_supplies", request, reply)
	if err != nil {
		return HaulRead{}, raw, err
	}
	if reply.GetFailure() != nil {
		return HaulRead{}, raw, failure(reply.GetFailure(), raw)
	}
	result, err := decodeHaulTargets(reply, identity, cell)
	return result, raw, err
}
func decodeHaulTargets(reply *o.ListSuppliesReply, identity *c.Identity, cell domain.Cell) (HaulRead, error) {
	if reply == nil || buildingUnknown(reply) != nil {
		return HaulRead{}, contract("invalid haul reply")
	}
	v := reply.GetObserved()
	if v == nil || buildingContext(v.Context, identity, 0, false) != nil || len(v.Stocks) > 256 {
		return HaulRead{}, contract("haul census unavailable")
	}
	complete, err := emergencyCompleteness(v.Completeness, len(v.Stocks))
	if yes, known := complete.Value(); err != nil || !known || !yes {
		return HaulRead{}, contract("incomplete haul census")
	}
	out := HaulRead{Context: proto.Clone(v.Context).(*c.ObservationContext), Targets: []HaulCandidate{}}
	seen := map[string]bool{}
	for _, stock := range v.Stocks {
		if stock == nil || stock.Units == nil || stock.GetUnits() < 0 || len(stock.Items) > 256 {
			return HaulRead{}, contract("invalid haul stock")
		}
		complete, err = emergencyCompleteness(stock.ItemsCompleteness, len(stock.Items))
		if yes, known := complete.Value(); err != nil || !known || !yes {
			return HaulRead{}, contract("incomplete haul items")
		}
		for _, item := range stock.Items {
			if item == nil || validID(item.GetId()) != nil || seen[item.GetId()] || item.MapId == nil || item.GetMapId() != identity.GetMapId() || item.Position == nil || item.Position.X == nil || item.Position.Z == nil || item.Position.GetX() != cell.X || item.Position.GetZ() != cell.Z || item.GetDefName() != stock.GetDefinition().GetDefName() {
				return HaulRead{}, contract("haul entity mismatch")
			}
			seen[item.GetId()] = true
			if len(seen) > 256 {
				return HaulRead{}, contract("haul item census exceeds bound")
			}
			// Native issues distinguish ineligible items from an incomplete census.
			if item.Snapshot == nil {
				continue
			}
			if item.Snapshot.GetEntityId() != item.GetId() || !proto.Equal(item.Snapshot.Context, v.Context) || validID(item.Snapshot.GetToken()) != nil {
				return HaulRead{}, contract("haul CAS scope mismatch")
			}
			out.Targets = append(out.Targets, HaulCandidate{Thing: item.GetId(), Definition: item.GetDefName(), Cell: cell, Token: item.Snapshot.GetToken()})
			if len(out.Targets) > 256 {
				return HaulRead{}, contract("haul targets exceed bound")
			}
		}
	}
	return out, nil
}
