package bridge

import (
	"context"
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// PackedItem is one spawned, unheld packed (minified) item and the building
// inside it; a RelocateIntent on Inner installs it (#830).
// Quality (QualityCategory ordinal, QualityKnown false without one) and
// MarketValue rank art stock for the trade selector (#1194).
type PackedItem struct {
	ID, Inner, InnerDef string
	Quality             int32
	QualityKnown        bool
	MarketValue         float64
}

// validQuality is a QualityCategory ordinal, Awful (0) to Legendary (6).
func validQuality(q int32) bool { return q >= 0 && q <= 6 }

// ReadPackedItems lists the colony's spawned, unheld packed items of one
// packed definition.
func (client *Client) ReadPackedItems(ctx context.Context, identity *c.Identity, packedDef string) ([]PackedItem, Result, error) {
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
	var out []PackedItem
	for _, row := range snapshot.Stocks {
		if row == nil || row.Definition.GetDefName() != packedDef {
			return nil, raw, contract("invalid packed items row")
		}
		for _, item := range row.Items {
			if validID(item.GetId()) != nil || validID(item.GetInnerId()) != nil || validID(item.GetInnerDefName()) != nil ||
				item.Quality != nil && !validQuality(item.GetQuality()) || math.IsNaN(item.GetMarketValue()) || math.IsInf(item.GetMarketValue(), 0) || item.GetMarketValue() < 0 {
				return nil, raw, contract("invalid packed item")
			}
			out = append(out, PackedItem{ID: item.GetId(), Inner: item.GetInnerId(), InnerDef: item.GetInnerDefName(),
				Quality: item.GetQuality(), QualityKnown: item.Quality != nil, MarketValue: item.GetMarketValue()})
		}
	}
	return out, raw, nil
}
