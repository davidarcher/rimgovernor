package bridge

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

func resourceSourcesContext() *c.ObservationContext {
	return &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(7), NativeGeneration: proto.Uint64(1)}
}

func TestReadResourceSourcesDecodesAndOrdersByDistance(t *testing.T) {
	reply := &o.ResourceSourcesReply{Outcome: &o.ResourceSourcesReply_Observed{Observed: &o.ResourceSourcesSnapshot{
		Context:  resourceSourcesContext(),
		Resource: proto.String("Steel"),
		Sources: []*o.ResourceSource{
			{Source: &o.EntityRef{Id: proto.String("rock2"), Position: &c.Cell{X: proto.Int32(5), Z: proto.Int32(6)},
				Snapshot: &o.SnapshotRef{Token: proto.String("mine-tok2")}}, Method: proto.String("mine"), Yield: proto.Float64(20),
				Distance: proto.Float64(9), Designated: proto.Bool(false), Safety: proto.String("open_surface")},
			{Source: &o.EntityRef{Id: proto.String("rock1"), Position: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)},
				Snapshot: &o.SnapshotRef{Token: proto.String("mine-tok1")}}, Method: proto.String("mine"), Yield: proto.Float64(15),
				Distance: proto.Float64(3), Designated: proto.Bool(false), Safety: proto.String("open_surface")},
		},
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}},
	}}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != "rimgovernor/observations_list_resource_sources" {
			t.Fatal(arg.Tool)
		}
		return pbResult(reply), nil
	}}, time.Second)
	rows, _, err := client.ReadResourceSources(context.Background(), pbIdentity(), "Steel")
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 2 || rows[0].ThingID != "rock1" || rows[0].Yield != 15 || rows[1].ThingID != "rock2" || rows[1].Yield != 20 {
		t.Fatal(rows)
	}
	if rows[0].Method != policy.ResourceSourceMine || rows[0].Safety != "open_surface" {
		t.Fatal(rows[0])
	}
	if rows[0].Cell.X != 1 || rows[0].Cell.Z != 2 || rows[0].Token != "mine-tok1" {
		t.Fatal(rows[0])
	}
	if rows[1].Cell.X != 5 || rows[1].Cell.Z != 6 || rows[1].Token != "mine-tok2" {
		t.Fatal(rows[1])
	}
}

func TestReadResourceSourcesRejectsMineSourceMissingSnapshot(t *testing.T) {
	reply := &o.ResourceSourcesReply{Outcome: &o.ResourceSourcesReply_Observed{Observed: &o.ResourceSourcesSnapshot{
		Context:  resourceSourcesContext(),
		Resource: proto.String("Steel"),
		Sources: []*o.ResourceSource{
			{Source: &o.EntityRef{Id: proto.String("rock1")}, Method: proto.String("mine"), Yield: proto.Float64(15),
				Distance: proto.Float64(3), Designated: proto.Bool(false), Safety: proto.String("open_surface")},
		},
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}},
	}}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(reply), nil
	}}, time.Second)
	if _, _, err := client.ReadResourceSources(context.Background(), pbIdentity(), "Steel"); err == nil {
		t.Fatal("expected missing mine snapshot rejection")
	}
}

func TestReadResourceSourcesRejectsIncompleteCensus(t *testing.T) {
	reply := &o.ResourceSourcesReply{Outcome: &o.ResourceSourcesReply_Observed{Observed: &o.ResourceSourcesSnapshot{
		Context:      resourceSourcesContext(),
		Resource:     proto.String("Steel"),
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(false)}},
	}}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(reply), nil
	}}, time.Second)
	if _, _, err := client.ReadResourceSources(context.Background(), pbIdentity(), "Steel"); err == nil {
		t.Fatal("expected incomplete census rejection")
	}
}

func TestReadResourceSourcesRejectsFractionalYield(t *testing.T) {
	reply := &o.ResourceSourcesReply{Outcome: &o.ResourceSourcesReply_Observed{Observed: &o.ResourceSourcesSnapshot{
		Context:  resourceSourcesContext(),
		Resource: proto.String("Steel"),
		Sources: []*o.ResourceSource{
			{Source: &o.EntityRef{Id: proto.String("rock1")}, Method: proto.String("mine"), Yield: proto.Float64(15.5),
				Distance: proto.Float64(3), Designated: proto.Bool(false), Safety: proto.String("open_surface")},
		},
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}},
	}}}
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(reply), nil
	}}, time.Second)
	if _, _, err := client.ReadResourceSources(context.Background(), pbIdentity(), "Steel"); err == nil {
		t.Fatal("expected fractional yield rejection")
	}
}
