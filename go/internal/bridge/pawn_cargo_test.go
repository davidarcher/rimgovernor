package bridge

import (
	"context"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestPawnCargoUsesOnlyNamedInventoryInCompleteHomeCensus(t *testing.T) {
	stock := &o.ResourceStock{Definition: &o.DefinitionRef{DefName: proto.String("Steel")}, Ours: proto.Int64(999), Holders: []*o.HeldStock{
		{Holder: &c.Ref{Id: proto.String("crew")}, HolderKind: o.HolderKind_HOLDER_KIND_PAWN_INVENTORY.Enum(), Units: proto.Int64(5)},
		{Holder: &c.Ref{Id: proto.String("crew")}, HolderKind: o.HolderKind_HOLDER_KIND_CARRIED.Enum(), Units: proto.Int64(2)},
		{Holder: &c.Ref{Id: proto.String("other")}, HolderKind: o.HolderKind_HOLDER_KIND_PAWN_INVENTORY.Enum(), Units: proto.Int64(40)},
		{Holder: &c.Ref{Id: proto.String("crew")}, HolderKind: o.HolderKind_HOLDER_KIND_CONTAINER.Enum(), Units: proto.Int64(100)}}}
	snapshot := &o.SuppliesSnapshot{Context: pbContext(), Completeness: &o.Completeness{Filtered: proto.Uint64(4)}, Stocks: []*o.ResourceStock{stock}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool != "rimgovernor/observations_list_supplies" {
			t.Fatal(arg.Tool)
		}
		return pbResult(&o.ListSuppliesReply{Outcome: &o.ListSuppliesReply_Observed{Observed: snapshot}}), nil
	}}, testBudget)
	cargo, _, err := client.ReadPawnCargo(context.Background(), pbIdentity(), []string{"crew"})
	if err != nil || cargo["Steel"] != 7 {
		t.Fatal(cargo, err)
	}
	snapshot.Completeness = nil
	if _, _, err = client.ReadPawnCargo(context.Background(), pbIdentity(), []string{"crew"}); err == nil {
		t.Fatal("unknown census admitted")
	}
}
