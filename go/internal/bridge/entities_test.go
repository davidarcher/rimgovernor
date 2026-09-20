package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
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

func zonesPageReply(rows []*o.ZoneState, complete bool, next string) *o.ListZonesReply {
	s := &o.ZonesSnapshot{Context: pbContext(), Zones: rows, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(complete)}, Matched: proto.Uint64(uint64(len(rows))), Returned: proto.Uint64(uint64(len(rows))), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}
	if next != "" {
		s.Completeness.Page.NextCursor = proto.String(next)
	}
	return &o.ListZonesReply{Outcome: &o.ListZonesReply_Observed{Observed: s}}
}

// A full read pages to the end and keys every row by id; the request
// asks for no cells, contents or filter and no since tick.
func TestReadZonesPagesAFullRead(t *testing.T) {
	var cursors []string
	server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		request := decodeZonesRequest(t, arg)
		cursors = append(cursors, request.Page.GetCursor())
		if request.ChangedSinceTick != nil || request.GetIncludeCells() || request.Page.GetLimit() != zonesPage || !proto.Equal(request.Scope.ExpectedIdentity, pbIdentity()) {
			t.Fatal("query differs", request)
		}
		if request.Page.GetCursor() == "" {
			return pbResult(zonesPageReply([]*o.ZoneState{zoneRow("Zone_1"), zoneRow("Zone_2")}, false, "c1")), nil
		}
		return pbResult(zonesPageReply([]*o.ZoneState{zoneRow("Zone_3")}, true, "")), nil
	}}
	client := testClient(t, server, testBudget)
	rows, _, err := client.ReadZones(context.Background(), pbIdentity(), 0)
	if err != nil || len(cursors) != 2 || cursors[1] != "c1" || len(rows.Rows) != 3 || rows.Delta || rows.Unchanged != 0 || rows.AsOf() != pbContext().GetTick() {
		t.Fatalf("%+v %v cursors=%v", rows, err, cursors)
	}
	if rows.Rows["Zone_2"].GetLabel() != "zone Zone_2" {
		t.Fatal(rows.Rows)
	}
}

// A delta read carries the since tick; the reply's as_of_tick marks it a
// delta with its unchanged count and removed ids. Without as_of_tick (an
// older native) the same ask is a full read.
func TestReadZonesDelta(t *testing.T) {
	stamp := true
	server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		request := decodeZonesRequest(t, arg)
		if request.GetChangedSinceTick() != 40 {
			t.Fatal("since differs", request)
		}
		reply := zonesPageReply([]*o.ZoneState{zoneRow("Zone_2")}, true, "")
		if stamp {
			s := reply.GetObserved()
			s.AsOfTick, s.Unchanged, s.RemovedIds = proto.Int64(pbContext().GetTick()), proto.Uint32(4), []string{"Zone_9"}
		}
		return pbResult(reply), nil
	}}
	client := testClient(t, server, testBudget)
	rows, _, err := client.ReadZones(context.Background(), pbIdentity(), 40)
	if err != nil || !rows.Delta || rows.Unchanged != 4 || len(rows.Removed) != 1 || rows.Removed[0] != "Zone_9" || len(rows.Rows) != 1 {
		t.Fatalf("%+v %v", rows, err)
	}
	stamp = false
	rows, _, err = client.ReadZones(context.Background(), pbIdentity(), 40)
	if err != nil || rows.Delta || rows.Unchanged != 0 || len(rows.Removed) != 0 || len(rows.Rows) != 1 {
		t.Fatalf("old native: %+v %v", rows, err)
	}
}

func TestReadZonesRefusalsAndContractFaults(t *testing.T) {
	for name, tc := range map[string]struct {
		since int64
		reply func() *o.ListZonesReply
		want  []error
	}{
		"expired delta": {40, func() *o.ListZonesReply {
			return &o.ListZonesReply{Outcome: &o.ListZonesReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_STALE.Enum(), Detail: proto.String("changed_since_tick is older than the tombstone window.")}}}
		}, []error{ErrDeltaExpired, ErrUnavailable}},
		"stale on a full read is not expiry": {0, func() *o.ListZonesReply {
			return &o.ListZonesReply{Outcome: &o.ListZonesReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_STALE.Enum()}}}
		}, []error{ErrUnavailable}},
		"refused": {0, func() *o.ListZonesReply {
			return &o.ListZonesReply{Outcome: &o.ListZonesReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum()}}}
		}, []error{ErrRefused}},
		"unchanged without an ask": {0, func() *o.ListZonesReply {
			r := zonesPageReply(nil, true, "")
			r.GetObserved().Unchanged = proto.Uint32(2)
			return r
		}, []error{ErrContract}},
		"removed without an ask": {0, func() *o.ListZonesReply {
			r := zonesPageReply(nil, true, "")
			r.GetObserved().RemovedIds = []string{"Zone_1"}
			return r
		}, []error{ErrContract}},
		"as_of off the context": {40, func() *o.ListZonesReply {
			r := zonesPageReply(nil, true, "")
			r.GetObserved().AsOfTick = proto.Int64(pbContext().GetTick() + 1)
			return r
		}, []error{ErrContract}},
		"removed and listed": {40, func() *o.ListZonesReply {
			r := zonesPageReply([]*o.ZoneState{zoneRow("Zone_1")}, true, "")
			r.GetObserved().AsOfTick, r.GetObserved().RemovedIds = proto.Int64(pbContext().GetTick()), []string{"Zone_1"}
			return r
		}, []error{ErrContract}},
		"duplicate": {0, func() *o.ListZonesReply {
			return zonesPageReply([]*o.ZoneState{zoneRow("Zone_1"), zoneRow("Zone_1")}, true, "")
		}, []error{ErrContract}},
		"incomplete without a cursor": {0, func() *o.ListZonesReply {
			return zonesPageReply([]*o.ZoneState{zoneRow("Zone_1")}, false, "")
		}, []error{ErrContract}},
		"other identity": {0, func() *o.ListZonesReply {
			r := zonesPageReply(nil, true, "")
			r.GetObserved().Context.Identity.LoadToken = proto.String("other")
			return r
		}, []error{ErrContract}},
	} {
		t.Run(name, func(t *testing.T) {
			server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(tc.reply()), nil
			}}
			client := testClient(t, server, testBudget)
			rows, _, err := client.ReadZones(context.Background(), pbIdentity(), tc.since)
			for _, want := range tc.want {
				if !errors.Is(err, want) {
					t.Fatalf("%+v %v, want %v", rows, err, want)
				}
			}
			if rows.Rows != nil {
				t.Fatal(rows)
			}
		})
	}
}

func TestMergeEntitiesAndDrift(t *testing.T) {
	held := map[string]*o.ZoneState{"Zone_1": zoneRow("Zone_1"), "Zone_2": zoneRow("Zone_2"), "Zone_3": zoneRow("Zone_3")}
	changed := zoneRow("Zone_2")
	changed.Label = proto.String("renamed")
	delta := EntityRows[*o.ZoneState]{Delta: true, Rows: map[string]*o.ZoneState{"Zone_2": changed, "Zone_4": zoneRow("Zone_4")}, Removed: []string{"Zone_3"}, Unchanged: 1}
	merged := MergeEntities(held, delta)
	if len(merged) != 3 || merged["Zone_2"].GetLabel() != "renamed" || merged["Zone_3"] != nil || merged["Zone_4"] == nil || merged["Zone_1"] != held["Zone_1"] {
		t.Fatal(merged)
	}
	if len(held) != 3 || held["Zone_2"].GetLabel() != "zone Zone_2" {
		t.Fatal("the held rows were changed in place")
	}
	// A full read replaces outright.
	full := EntityRows[*o.ZoneState]{Rows: map[string]*o.ZoneState{"Zone_7": zoneRow("Zone_7")}}
	if got := MergeEntities(held, full); len(got) != 1 || got["Zone_7"] == nil {
		t.Fatal(got)
	}
	if EntityDrift(merged, map[string]*o.ZoneState{"Zone_1": zoneRow("Zone_1"), "Zone_2": changed, "Zone_4": zoneRow("Zone_4")}) != 0 {
		t.Fatal("drift on an equal read")
	}
	// One row differing, one missing from the full read, one extra in it.
	other := zoneRow("Zone_2")
	if got := EntityDrift(merged, map[string]*o.ZoneState{"Zone_1": zoneRow("Zone_1"), "Zone_2": other, "Zone_5": zoneRow("Zone_5")}); got != 3 {
		t.Fatal(got)
	}
}
