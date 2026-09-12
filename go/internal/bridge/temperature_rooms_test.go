package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"math"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func temperatureTestSnapshot() *o.RoomsSnapshot {
	cell := &c.Cell{X: proto.Int32(3), Z: proto.Int32(7)}
	return &o.RoomsSnapshot{Context: authorityTestContext(7), Completeness: emergencyCounts(1), Rooms: []*o.RoomState{{Id: proto.String("42"), ProperRoom: proto.Bool(true), Doorway: proto.Bool(false), Outdoors: proto.Bool(false), PsychologicallyOutdoors: proto.Bool(false), TouchesMapEdge: proto.Bool(false), OpenRoofCount: proto.Uint32(0), CellCount: proto.Uint32(1), TemperatureC: proto.Float64(5), Center: cell, Extents: &o.Rectangle{Minimum: cell, Maximum: cell}, Cells: []*c.Cell{cell}, CellsCompleteness: emergencyCounts(1), Contents: []*o.Quantity{{DefName: proto.String("SleepingSpot"), Units: proto.Int64(1)}}, ContentsCompleteness: emergencyCounts(1), Beds: []*o.BuildingState{{Building: &o.EntityRef{Id: proto.String("bed"), DefName: proto.String("SleepingSpot"), MapId: proto.Int32(0), Position: cell}, Status: proto.String("built")}}}}}
}

func TestTemperatureRoomsTypedRead(t *testing.T) {
	id := pbIdentity()
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != "rimgovernor/observations_list_rooms" {
			t.Fatal(arg.Tool)
		}
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		request := &o.ListRoomsRequest{}
		if err := protojson.Unmarshal([]byte(outer.Request), request); err != nil {
			t.Fatal(err)
		}
		want := &o.ListRoomsRequest{Scope: &o.ReadScope{ExpectedIdentity: pbIdentity()}, IncludeOutdoors: proto.Bool(false), IncludeBoundary: proto.Bool(false), IncludeCells: proto.Bool(true), Page: &c.PageRequest{Limit: proto.Uint32(256)}}
		if !proto.Equal(request, want) {
			t.Fatal(request)
		}
		id.LoadToken = proto.String("mutated")
		return pbResult(&o.ListRoomsReply{Outcome: &o.ListRoomsReply_Observed{Observed: temperatureTestSnapshot()}}), nil
	}}, time.Second)
	if got, _, err := client.ReadTemperatureRooms(context.Background(), id); err != nil || len(got.GetObserved().GetRooms()) != 1 {
		t.Fatal(got, err)
	}
}

func TestTemperatureRoomsRejectMalformedEvidence(t *testing.T) {
	for name, mutate := range map[string]func(*o.RoomsSnapshot){
		"world":               func(v *o.RoomsSnapshot) { v.Context.Identity.LoadToken = proto.String("other") },
		"unknown-fields":      func(v *o.RoomsSnapshot) { v.ProtoReflect().SetUnknown([]byte{0x98, 0x06, 0x01}) },
		"page":                func(v *o.RoomsSnapshot) { v.Completeness.Page.Complete = proto.Bool(false) },
		"cells-incomplete":    func(v *o.RoomsSnapshot) { v.Rooms[0].CellsCompleteness.Unreadable = proto.Uint64(1) },
		"contents-incomplete": func(v *o.RoomsSnapshot) { v.Rooms[0].ContentsCompleteness.Matched = proto.Uint64(2) },
		"unknown-quantity":    func(v *o.RoomsSnapshot) { v.Rooms[0].Contents[0].Units = nil },
		"nan":                 func(v *o.RoomsSnapshot) { v.Rooms[0].TemperatureC = proto.Float64(math.NaN()) },
		"negative":            func(v *o.RoomsSnapshot) { v.Rooms[0].Contents[0].Units = proto.Int64(-1) },
		"roof":                func(v *o.RoomsSnapshot) { v.Rooms[0].OpenRoofCount = proto.Uint32(2) },
		"outdoors":            func(v *o.RoomsSnapshot) { v.Rooms[0].PsychologicallyOutdoors = proto.Bool(true) },
		"extents":             func(v *o.RoomsSnapshot) { v.Rooms[0].Extents.Maximum = &c.Cell{X: proto.Int32(8), Z: proto.Int32(7)} },
		"bed-outside": func(v *o.RoomsSnapshot) {
			v.Rooms[0].Beds[0].Building.Position = &c.Cell{X: proto.Int32(8), Z: proto.Int32(7)}
		},
		"bed-duplicate": func(v *o.RoomsSnapshot) { v.Rooms[0].Beds = append(v.Rooms[0].Beds, v.Rooms[0].Beds[0]) },
		"bed-map":       func(v *o.RoomsSnapshot) { v.Rooms[0].Beds[0].Building.MapId = proto.Int32(2) },
		"bed-status":    func(v *o.RoomsSnapshot) { v.Rooms[0].Beds[0].Status = proto.String("blueprint") },
	} {
		t.Run(name, func(t *testing.T) {
			v := temperatureTestSnapshot()
			mutate(v)
			if err := ValidateTemperatureRooms(v, pbIdentity()); err == nil {
				t.Fatal(v)
			}
		})
	}
}

func TestTemperatureRoomsTypedRefusalAndUnavailable(t *testing.T) {
	for _, unavailable := range []bool{false, true} {
		client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
			reply := &o.ListRoomsReply{Outcome: &o.ListRoomsReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum()}}}
			if unavailable {
				reply.Outcome = &o.ListRoomsReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_LIMIT_EXCEEDED.Enum()}}
			}
			result := pbResult(reply)
			result.IsError = !unavailable
			return result, nil
		}}, time.Second)
		want := ErrRefused
		if unavailable {
			want = ErrUnavailable
		}
		reply, _, err := client.ReadTemperatureRooms(context.Background(), pbIdentity())
		if reply == nil || !errors.Is(err, want) {
			t.Fatal(reply, err)
		}
	}
}
