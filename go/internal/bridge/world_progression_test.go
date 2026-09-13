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

func worldProgressionFixture() *o.WorldProgressionSnapshot {
	return &o.WorldProgressionSnapshot{
		Context:      pbContext(),
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}},
		Caravans: []*o.CaravanState{{
			Caravan: &o.EntityRef{Id: proto.String("caravan-1")},
			Tile:    proto.Int32(42), Moving: proto.Bool(true),
			Pawns: []*o.PawnState{{Pawn: &o.EntityRef{Id: proto.String("pawn-1")}}},
		}},
	}
}
func TestReadWorldProgressionAcceptsValidObservation(t *testing.T) {
	snapshot := worldProgressionFixture()
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != "rimgovernor/observations_read_world_progression" {
			t.Fatal(arg.Tool)
		}
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		q := &o.WorldProgressionRequest{}
		if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
			t.Fatal(err)
		}
		if !q.GetIncludeStorage() || q.Page.GetLimit() != 256 {
			t.Fatal(q)
		}
		return pbResult(&o.WorldProgressionReply{Outcome: &o.WorldProgressionReply_Observed{Observed: snapshot}}), nil
	}}, time.Second)
	out, raw, err := client.ReadWorldProgression(context.Background(), pbIdentity(), true)
	if err != nil || len(raw.Envelope) == 0 || len(out.Caravans) != 1 || out.Caravans[0].ID != "caravan-1" ||
		out.Caravans[0].Tile != 42 || !out.Caravans[0].Moving || len(out.Caravans[0].PawnIDs) != 1 || out.Caravans[0].PawnIDs[0] != "pawn-1" {
		t.Fatal(out, err)
	}
}
func TestReadWorldProgressionRejectsInvalidInputs(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("invalid request dispatched")
		return nil, nil
	}}, time.Second)
	if _, _, err := client.ReadWorldProgression(context.Background(), nil, false); err == nil {
		t.Fatal("expected rejection")
	}
}
func TestReadWorldProgressionMalformedEvidence(t *testing.T) {
	edits := map[string]func(*o.WorldProgressionSnapshot){
		"world":               func(v *o.WorldProgressionSnapshot) { v.Context.Identity.LoadToken = proto.String("other") },
		"partial page":        func(v *o.WorldProgressionSnapshot) { v.Completeness.Page.Complete = proto.Bool(false) },
		"missing caravan id":  func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Caravan.Id = nil },
		"duplicate caravan":   func(v *o.WorldProgressionSnapshot) { v.Caravans = append(v.Caravans, v.Caravans[0]) },
		"negative tile":       func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Tile = proto.Int32(-1) },
		"missing pawn id":     func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Pawns[0].Pawn.Id = nil },
		"duplicate pawn":      func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Pawns = append(v.Caravans[0].Pawns, v.Caravans[0].Pawns[0]) },
	}
	for name, edit := range edits {
		t.Run(name, func(t *testing.T) {
			snapshot := worldProgressionFixture()
			edit(snapshot)
			client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&o.WorldProgressionReply{Outcome: &o.WorldProgressionReply_Observed{Observed: snapshot}}), nil
			}}, time.Second)
			if _, _, err := client.ReadWorldProgression(context.Background(), pbIdentity(), false); err == nil {
				t.Fatal("malformed world progression accepted")
			}
		})
	}
}
