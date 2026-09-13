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
			{Source: &o.EntityRef{Id: proto.String("rock2")}, Method: proto.String("mine"), Yield: proto.Float64(20),
				Distance: proto.Float64(9), Designated: proto.Bool(false), Safety: proto.String("open_surface")},
			{Source: &o.EntityRef{Id: proto.String("rock1")}, Method: proto.String("mine"), Yield: proto.Float64(15),
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
