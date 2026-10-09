package bridge

import (
	"context"
	"slices"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ReadNeuroformerItems lists the exact ids of the player's unforbidden stacks
// of def lying on the map, sorted: the item a colonist uses on
// itself (UseItem). The royalty read carries only the count and lags this
// read. A stack with any forbidden unit is left out, since the supplies row
// counts forbidden units without naming them.
func (client *Client) ReadNeuroformerItems(ctx context.Context, identity *c.Identity, def string) ([]string, Result, error) {
	if err := ValidateIdentity(identity); err != nil || validID(def) != nil {
		return nil, Result{}, contract("invalid neuroformer scope")
	}
	request := &o.ListSuppliesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)},
		Filter: &o.StockFilter{DefNames: []string{def}, Ownership: o.StockOwnership_STOCK_OWNERSHIP_OURS.Enum(), IncludeHeld: proto.Bool(false)}}
	reply := &o.ListSuppliesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_supplies", request, reply)
	if err != nil {
		return nil, raw, err
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	items, err := decodeNeuroformerItems(reply, identity, def)
	return items, raw, err
}

func decodeNeuroformerItems(reply *o.ListSuppliesReply, identity *c.Identity, def string) ([]string, error) {
	if reply == nil || buildingUnknown(reply) != nil {
		return nil, contract("invalid neuroformer reply")
	}
	v := reply.GetObserved()
	if v == nil || buildingContext(v.Context, identity, 0, false) != nil {
		return nil, contract("neuroformer census unavailable")
	}
	if v.Completeness == nil || v.Completeness.GetFiltered() != 0 {
		return nil, contract("incomplete neuroformer census")
	}
	out := []string{}
	seen := map[string]bool{}
	for _, stock := range v.Stocks {
		if stock == nil || stock.Units == nil || stock.Forbidden == nil || stock.GetUnits() < 0 || stock.GetForbidden() < 0 || stock.GetForbidden() > stock.GetUnits() || stock.GetDefinition().GetDefName() != def {
			return nil, contract("invalid neuroformer stock")
		}
		for _, item := range stock.Items {
			id := item.GetItem().GetId()
			if !validRef(item.GetItem()) || seen[id] {
				return nil, contract("neuroformer entity mismatch")
			}
			seen[id] = true
			// Native issues distinguish ineligible items from an incomplete census.
			if item.Snapshot == nil || stock.GetForbidden() != 0 {
				continue
			}
			if !refSnapshot(item.Snapshot, item.GetItem(), v.Context) {
				return nil, contract("neuroformer CAS scope mismatch")
			}
			out = append(out, id)
		}
	}
	slices.Sort(out)
	return out, nil
}
