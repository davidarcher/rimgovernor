package bridge

import (
	"context"
	"testing"
	"time"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func neuroformerRead(forbidden int64) *o.ListSuppliesReply {
	v := supplyTestRead()
	stock := v.GetObserved().Stocks[0]
	stock.Definition.DefName = proto.String("PsychicAmplifier")
	stock.Forbidden = proto.Int64(forbidden)
	stock.Items[0].Item.Id = proto.String("PsychicAmplifier7")
	stock.Items[0].Snapshot.EntityId = proto.String("PsychicAmplifier7")
	return v
}

func TestReadNeuroformerItemsListsUnforbiddenStacks(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		protoTestRequest(t, arg, &o.ListSuppliesRequest{Scope: &o.ReadScope{ExpectedIdentity: pbIdentity()}, Filter: &o.StockFilter{DefNames: []string{"PsychicAmplifier"}, Ownership: o.StockOwnership_STOCK_OWNERSHIP_OURS.Enum(), IncludeHeld: proto.Bool(false)}})
		return pbResult(neuroformerRead(0)), nil
	}}, time.Second)
	items, _, err := client.ReadNeuroformerItems(context.Background(), pbIdentity(), "PsychicAmplifier")
	if err != nil || len(items) != 1 || items[0] != "PsychicAmplifier7" {
		t.Fatal(items, err)
	}
}

func TestDecodeNeuroformerItemsRefusals(t *testing.T) {
	// A stack with a forbidden unit names no usable item.
	items, err := decodeNeuroformerItems(neuroformerRead(1), pbIdentity(), "PsychicAmplifier")
	if err != nil || len(items) != 0 {
		t.Fatal(items, err)
	}
	for name, edit := range map[string]func(*o.SuppliesSnapshot){
		"other def": func(v *o.SuppliesSnapshot) { v.Stocks[0].Definition.DefName = proto.String("Steel") },
		"duplicate": func(v *o.SuppliesSnapshot) {
			v.Stocks[0].Items = append(v.Stocks[0].Items, proto.Clone(v.Stocks[0].Items[0]).(*o.StockItem))
		},
		"incomplete": func(v *o.SuppliesSnapshot) { v.Completeness = nil },
		"scope":      func(v *o.SuppliesSnapshot) { v.Stocks[0].Items[0].Snapshot.EntityId = proto.String("foreign") },
	} {
		t.Run(name, func(t *testing.T) {
			v := neuroformerRead(0)
			edit(v.GetObserved())
			if _, err := decodeNeuroformerItems(v, pbIdentity(), "PsychicAmplifier"); err == nil {
				t.Fatal("accepted a bad census")
			}
		})
	}
}
