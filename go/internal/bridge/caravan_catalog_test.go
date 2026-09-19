package bridge

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func caravanCatalogFixture() *o.CaravanCatalog {
	return &o.CaravanCatalog{
		Snapshot:     &o.SnapshotRef{Context: pbContext(), Token: proto.String("catalog-token")},
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}},
		CargoGroups: []*o.CargoGroup{
			{GroupId: proto.String("group-1"), DefName: proto.String("Steel"), Count: proto.Int64(50)},
			{GroupId: proto.String("group-2"), DefName: proto.String("Pemmican"), Count: proto.Int64(20), Nutrition: proto.Float64(0.05), Perishable: proto.Bool(true), RotDays: proto.Float64(60), Reserve: proto.Bool(true), EaterIds: []string{"pawn-1", "pawn-2"}},
		},
		Routes: []*o.WorldRoute{{Destination: proto.Int32(42), Reachable: proto.Bool(true), EstimatedTicks: proto.Int64(1000)}},
	}
}
func TestReadCaravanCatalogAcceptsValidObservation(t *testing.T) {
	catalog := caravanCatalogFixture()
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != "rimgovernor/observations_read_caravan_catalog" {
			t.Fatal(arg.Tool)
		}
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		q := &o.CaravanCatalogRequest{}
		if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
			t.Fatal(err)
		}
		if q.GetDestination() != 42 || q.Page.GetLimit() != 256 {
			t.Fatal(q)
		}
		return pbResult(&o.CaravanCatalogReply{Outcome: &o.CaravanCatalogReply_Observed{Observed: catalog}}), nil
	}}, time.Second)
	out, raw, err := client.ReadCaravanCatalog(context.Background(), pbIdentity(), 42)
	if err != nil || len(raw.Envelope) == 0 || out.Token != "catalog-token" || len(out.CargoGroups) != 2 || len(out.Routes) != 1 {
		t.Fatal(out, err)
	}
}
func TestReadCaravanCatalogRejectsInvalidInputs(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("invalid request dispatched")
		return nil, nil
	}}, time.Second)
	if _, _, err := client.ReadCaravanCatalog(context.Background(), pbIdentity(), -1); err == nil {
		t.Fatal("expected rejection")
	}
}
func TestReadCaravanCatalogMalformedEvidence(t *testing.T) {
	edits := map[string]func(*o.CaravanCatalog){
		"world":               func(v *o.CaravanCatalog) { v.Snapshot.Context.Identity.LoadToken = proto.String("other") },
		"missing token":       func(v *o.CaravanCatalog) { v.Snapshot.Token = nil },
		"partial page":        func(v *o.CaravanCatalog) { v.Completeness.Page.Complete = proto.Bool(false) },
		"duplicate group id":  func(v *o.CaravanCatalog) { v.CargoGroups = append(v.CargoGroups, v.CargoGroups[0]) },
		"missing destination": func(v *o.CaravanCatalog) { v.Routes[0].Destination = proto.Int32(1) },
		"negative estimate":   func(v *o.CaravanCatalog) { v.Routes[0].EstimatedTicks = proto.Int64(-1) },
		"negative count":      func(v *o.CaravanCatalog) { v.CargoGroups[0].Count = proto.Int64(-1) },
		"zero nutrition":      func(v *o.CaravanCatalog) { v.CargoGroups[1].Nutrition = proto.Float64(0) },
		"rot without perish":  func(v *o.CaravanCatalog) { v.CargoGroups[1].Perishable = proto.Bool(false) },
		"negative rot days":   func(v *o.CaravanCatalog) { v.CargoGroups[1].RotDays = proto.Float64(-1) },
		"duplicate eater":     func(v *o.CaravanCatalog) { v.CargoGroups[1].EaterIds = []string{"pawn-1", "pawn-1"} },
	}
	for name, edit := range edits {
		t.Run(name, func(t *testing.T) {
			catalog := caravanCatalogFixture()
			edit(catalog)
			client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&o.CaravanCatalogReply{Outcome: &o.CaravanCatalogReply_Observed{Observed: catalog}}), nil
			}}, time.Second)
			if _, _, err := client.ReadCaravanCatalog(context.Background(), pbIdentity(), 42); err == nil {
				t.Fatal("malformed catalog accepted")
			}
		})
	}
}
