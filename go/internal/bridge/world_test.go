package bridge

import (
	"context"
	"encoding/json"
	"math"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func worldFixture() *o.WorldSnapshot {
	return &o.WorldSnapshot{
		Context:      pbContext(),
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}},
		Settlements: []*o.Settlement{{
			Id: proto.String("settlement-1"), Label: proto.String("Outpost"), Tile: proto.Int32(42), Player: proto.Bool(false),
			FactionId: proto.String("faction-1"), FactionDefName: proto.String("Tribe"), Relation: proto.String("Neutral"), Goodwill: proto.Int32(10),
			Snapshot:        &o.SnapshotRef{Context: pbContext(), EntityId: proto.String("settlement-1"), Token: proto.String("settlement-cas")},
			FactionSnapshot: &o.SnapshotRef{Context: pbContext(), EntityId: proto.String("faction-1"), Token: proto.String("faction-cas")},
		}},
	}
}

// TestReadWorldDecodesLongitude covers the map-longitude wire-decode half of
// the EnsureMood-* schedule-fencing gap: boundary.ExpectedScheduleDef needs
// (tick, longitude, schedule slots) and longitude lives on WorldTile, not yet
// joined into the routine/mood census pipeline -- this only verifies the
// bridge-level decode used to eventually supply that value.
func TestReadWorldDecodesLongitude(t *testing.T) {
	for _, test := range []struct {
		name      string
		edit      func(*o.WorldSnapshot)
		known     bool
		longitude float64
		invalid   bool
	}{
		{"missing tile", func(v *o.WorldSnapshot) {}, false, 0, false},
		{"missing longitude", func(v *o.WorldSnapshot) { v.Tile = &o.WorldTile{} }, false, 0, false},
		{"known longitude", func(v *o.WorldSnapshot) { v.Tile = &o.WorldTile{Longitude: proto.Float64(-73.5)} }, true, -73.5, false},
		{"nan longitude", func(v *o.WorldSnapshot) { v.Tile = &o.WorldTile{Longitude: proto.Float64(math.NaN())} }, false, 0, true},
		{"out of range longitude", func(v *o.WorldSnapshot) { v.Tile = &o.WorldTile{Longitude: proto.Float64(200)} }, false, 0, true},
	} {
		t.Run(test.name, func(t *testing.T) {
			snapshot := worldFixture()
			test.edit(snapshot)
			client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&o.WorldReply{Outcome: &o.WorldReply_Observed{Observed: snapshot}}), nil
			}}, time.Second)
			out, _, err := client.ReadWorld(context.Background(), pbIdentity(), 42, 0)
			if test.invalid {
				if err == nil {
					t.Fatal("expected invalid longitude rejected")
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			lon, known := out.Longitude.Value()
			if known != test.known || (known && lon != test.longitude) {
				t.Fatal(lon, known)
			}
		})
	}
}

func TestReadWorldAcceptsValidObservation(t *testing.T) {
	snapshot := worldFixture()
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != "rimgovernor/observations_read_world" {
			t.Fatal(arg.Tool)
		}
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		q := &o.WorldRequest{}
		if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
			t.Fatal(err)
		}
		if q.GetTile() != 42 || q.GetSettlementRadius() != 0 || q.Page.GetLimit() != 256 {
			t.Fatal(q)
		}
		return pbResult(&o.WorldReply{Outcome: &o.WorldReply_Observed{Observed: snapshot}}), nil
	}}, time.Second)
	out, raw, err := client.ReadWorld(context.Background(), pbIdentity(), 42, 0)
	if err != nil || len(raw.Envelope) == 0 || len(out.Settlements) != 1 {
		t.Fatal(out, err)
	}
	row := out.Settlements[0]
	if row.ID != "settlement-1" || row.Label != "Outpost" || row.Tile != 42 || row.Player ||
		row.FactionID != "faction-1" || row.FactionDefName != "Tribe" || row.Relation != "Neutral" ||
		!row.GoodwillKnown || row.Goodwill != 10 || row.SnapshotToken != "settlement-cas" || row.FactionSnapshotToken != "faction-cas" {
		t.Fatal(row)
	}
}

func TestReadWorldRejectsInvalidInputs(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("invalid request dispatched")
		return nil, nil
	}}, time.Second)
	if _, _, err := client.ReadWorld(context.Background(), nil, 0, 0); err == nil {
		t.Fatal("expected rejection")
	}
	if _, _, err := client.ReadWorld(context.Background(), pbIdentity(), -1, 0); err == nil {
		t.Fatal("negative tile accepted")
	}
	if _, _, err := client.ReadWorld(context.Background(), pbIdentity(), 0, -1); err == nil {
		t.Fatal("negative radius accepted")
	}
}

func TestReadWorldMalformedEvidence(t *testing.T) {
	edits := map[string]func(*o.WorldSnapshot){
		"world":                    func(v *o.WorldSnapshot) { v.Context.Identity.LoadToken = proto.String("other") },
		"partial page":             func(v *o.WorldSnapshot) { v.Completeness.Page.Complete = proto.Bool(false) },
		"missing settlement id":    func(v *o.WorldSnapshot) { v.Settlements[0].Id = nil },
		"duplicate settlement":     func(v *o.WorldSnapshot) { v.Settlements = append(v.Settlements, v.Settlements[0]) },
		"missing settlement token": func(v *o.WorldSnapshot) { v.Settlements[0].Snapshot = nil },
		"settlement token entity mismatch": func(v *o.WorldSnapshot) {
			v.Settlements[0].Snapshot.EntityId = proto.String("other")
		},
		"invalid settlement token": func(v *o.WorldSnapshot) { v.Settlements[0].Snapshot.Token = proto.String("") },
		"missing faction token":    func(v *o.WorldSnapshot) { v.Settlements[0].FactionSnapshot = nil },
		"faction token entity mismatch": func(v *o.WorldSnapshot) {
			v.Settlements[0].FactionSnapshot.EntityId = proto.String("other")
		},
		"invalid faction token": func(v *o.WorldSnapshot) { v.Settlements[0].FactionSnapshot.Token = proto.String("") },
	}
	for name, edit := range edits {
		t.Run(name, func(t *testing.T) {
			snapshot := worldFixture()
			edit(snapshot)
			client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&o.WorldReply{Outcome: &o.WorldReply_Observed{Observed: snapshot}}), nil
			}}, time.Second)
			if _, _, err := client.ReadWorld(context.Background(), pbIdentity(), 42, 0); err == nil {
				t.Fatal("malformed world accepted")
			}
		})
	}
}
