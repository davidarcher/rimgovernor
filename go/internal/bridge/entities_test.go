package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func decodeZonesRequest(t *testing.T, arg nativeArgument) *o.ListZonesRequest {
	t.Helper()
	if arg.Tool != "rimgovernor/observations_list_zones" {
		t.Fatal(arg.Tool)
	}
	var outer struct {
		Request string `json:"request"`
	}
	if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
		t.Fatal(err)
	}
	request := &o.ListZonesRequest{}
	if err := protojson.Unmarshal([]byte(outer.Request), request); err != nil {
		t.Fatal(err)
	}
	return request
}

func zoneRow(id string) *o.ZoneState {
	return &o.ZoneState{Id: proto.String(id), Label: proto.String("zone " + id), Type: proto.String("stockpile"), Bounds: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(1), Z: proto.Int32(1)}, Maximum: &c.Cell{X: proto.Int32(2), Z: proto.Int32(2)}}}
}

func zonesPageReply(rows []*o.ZoneState, complete bool) *o.ListZonesReply {
	s := &o.ZonesSnapshot{Context: pbContext(), Zones: rows, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}
	return &o.ListZonesReply{Outcome: &o.ListZonesReply_Observed{Observed: s}}
}

// A full read keys every row by id.
func TestReadZonesKeysAFullRead(t *testing.T) {
	calls := 0
	server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		request := decodeZonesRequest(t, arg)
		calls++
		if !proto.Equal(request.Scope.ExpectedIdentity, pbIdentity()) {
			t.Fatal("query differs", request)
		}
		return pbResult(zonesPageReply([]*o.ZoneState{zoneRow("Zone_1"), zoneRow("Zone_2"), zoneRow("Zone_3")}, true)), nil
	}}
	client := testClient(t, server, testBudget)
	rows, _, err := client.ReadZones(context.Background(), pbIdentity())
	if err != nil || calls != 1 || rows.Rows.Len() != 3 || rows.AsOf() != pbContext().GetTick() {
		t.Fatalf("%+v %v calls=%d", rows, err, calls)
	}
	if rows.Rows.At("Zone_2").GetLabel() != "zone Zone_2" {
		t.Fatal(rows.Rows)
	}
}

func TestReadZonesRefusalsAndContractFaults(t *testing.T) {
	for name, tc := range map[string]struct {
		reply func() *o.ListZonesReply
		want  []error
	}{
		"stale on a full read": {func() *o.ListZonesReply {
			return &o.ListZonesReply{Outcome: &o.ListZonesReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_STALE.Enum()}}}
		}, []error{ErrUnavailable}},
		"refused": {func() *o.ListZonesReply {
			return &o.ListZonesReply{Outcome: &o.ListZonesReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum()}}}
		}, []error{ErrRefused}},
		"duplicate": {func() *o.ListZonesReply {
			return zonesPageReply([]*o.ZoneState{zoneRow("Zone_1"), zoneRow("Zone_1")}, true)
		}, []error{ErrContract}},
		"other identity": {func() *o.ListZonesReply {
			r := zonesPageReply(nil, true)
			r.GetObserved().Context.Identity.LoadToken = proto.String("other")
			return r
		}, []error{ErrContract}},
	} {
		t.Run(name, func(t *testing.T) {
			server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
				return pbResult(tc.reply()), nil
			}}
			client := testClient(t, server, testBudget)
			rows, _, err := client.ReadZones(context.Background(), pbIdentity())
			for _, want := range tc.want {
				if !errors.Is(err, want) {
					t.Fatalf("%+v %v, want %v", rows, err, want)
				}
			}
			if rows.Rows.Len() != 0 {
				t.Fatal(rows)
			}
		})
	}
}
