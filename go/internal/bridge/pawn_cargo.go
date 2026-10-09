package bridge

import (
	"context"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
)

// ReadPawnCargo reuses the complete held-stock census. Only named home-map
// pawns' inventory/carry slots count; loose home stock cannot prove delivery.
func (client *Client) ReadPawnCargo(ctx context.Context, identity *c.Identity, pawns []string) (map[string]int64, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	request := &o.ListSuppliesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.CloneOf(identity)}, Filter: &o.StockFilter{Category: o.StockCategory_STOCK_CATEGORY_HAULABLE.Enum(), Ownership: o.StockOwnership_STOCK_OWNERSHIP_OURS.Enum(), IncludeHeld: proto.Bool(true)}}
	reply := &o.ListSuppliesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_supplies", request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return nil, raw, err
	}
	snapshot := reply.GetObserved()
	if snapshot == nil || ValidateContext(snapshot.Context) != nil || !sameIdentity(snapshot.Context.Identity, identity) || snapshot.Completeness == nil || snapshot.Completeness.Filtered == nil {
		return nil, raw, contract("unknown pawn cargo census")
	}
	crew := map[string]bool{}
	for _, id := range pawns {
		if validID(id) != nil || crew[id] {
			return nil, raw, contract("invalid cargo crew")
		}
		crew[id] = true
	}
	out := map[string]int64{}
	for _, stock := range snapshot.Stocks {
		if stock == nil || stock.Definition == nil || validID(stock.Definition.GetDefName()) != nil {
			return nil, raw, contract("invalid cargo definition")
		}
		for _, held := range stock.Holders {
			if held == nil || held.Holder == nil || held.Units == nil || held.GetUnits() < 0 || held.HolderKind == nil {
				return nil, raw, contract("unknown cargo holder")
			}
			if crew[held.Holder.GetId()] && (held.GetHolderKind() == o.HolderKind_HOLDER_KIND_PAWN_INVENTORY || held.GetHolderKind() == o.HolderKind_HOLDER_KIND_CARRIED) {
				if out[stock.Definition.GetDefName()] > math.MaxInt64-held.GetUnits() {
					return nil, raw, contract("cargo count overflow")
				}
				out[stock.Definition.GetDefName()] += held.GetUnits()
			}
		}
	}
	return out, raw, nil
}
