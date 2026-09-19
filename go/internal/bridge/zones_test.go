package bridge

import (
	"context"
	"encoding/json"
	"errors"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"testing"
	"time"
)

func zoneTestPage() *o.ZonesSnapshot {
	ctx := authorityTestContext(3000)
	ctx.Tick = proto.Int64(3000)
	return &o.ZonesSnapshot{Context: ctx, AsOfTick: proto.Int64(3000), Zones: []*o.ZoneState{{Id: proto.String("Zone_1"), FoodStorage: proto.Bool(true), Snapshot: &o.SnapshotRef{Context: ctx, EntityId: proto.String("Zone_1"), Token: proto.String("z")}}}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Returned: proto.Uint64(1), Matched: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}
}

func TestZoneRefusalFallsBackExactlyOnce(t *testing.T) {
	for _, reason := range []string{ZoneDeltaExpired, ZoneTrackingUnavailable, "other"} {
		t.Run(reason, func(t *testing.T) {
			var asks []int64
			client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
				var outer struct {
					Request string `json:"request"`
				}
				if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
					t.Fatal(err)
				}
				q := &o.ListZonesRequest{}
				if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
					t.Fatal(err)
				}
				asks = append(asks, q.GetChangedSinceTick())
				if len(asks) == 1 {
					if reason != "other" {
						return pbResult(&o.ListZonesReply{Outcome: &o.ListZonesReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_STALE.Enum(), Detail: proto.String(reason)}}}), nil
					}
					return pbResult(&o.ListZonesReply{Outcome: &o.ListZonesReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_UNAVAILABLE.Enum(), Detail: proto.String(reason)}}}), nil
				}
				if q.ChangedSinceTick != nil {
					t.Fatal("fallback retained delta tick")
				}
				return pbResult(&o.ListZonesReply{Outcome: &o.ListZonesReply_Observed{Observed: zoneTestPage()}}), nil
			}}, time.Second)
			got, _, err := client.ReadZoneSection(context.Background(), pbIdentity(), 10)
			if reason == "other" {
				if !errors.Is(err, ErrRefused) || len(asks) != 1 {
					t.Fatal(asks, err)
				}
				return
			}
			if err != nil || len(asks) != 2 || asks[1] != 0 || !got.Fallback || got.Delta || got.AsOf != 3000 || len(got.Rows) != 1 {
				t.Fatal(asks, got, err)
			}
		})
	}
}

func TestZonesMergeTombstonesAndDrift(t *testing.T) {
	row := func(id string, storage bool) *o.ZoneState {
		return &o.ZoneState{Id: proto.String(id), FoodStorage: proto.Bool(storage)}
	}
	held := ZonesRead{AsOf: 10, Rows: []*o.ZoneState{row("a", false), row("b", false), row("c", true)}}
	delta := ZonesRead{Delta: true, AsOf: 20, Rows: []*o.ZoneState{row("a", true), row("d", false)}, Removed: []string{"b"}, Unchanged: 1}
	got := MergeZones(held, delta)
	full := ZonesRead{AsOf: 20, Rows: []*o.ZoneState{row("a", true), row("c", true), row("d", false)}}
	if got.AsOf != 20 || ZoneDrift(got, full) != 0 || len(held.Rows) != 3 || held.Rows[0].GetFoodStorage() {
		t.Fatal(got, held)
	}
	if ZoneDrift(held, full) != 3 {
		t.Fatal("missing added removed or changed drift")
	}
	if got := MergeZones(held, ZonesRead{AsOf: 30, Fallback: true}); len(got.Rows) != 0 || got.AsOf != 30 {
		t.Fatal("full fallback did not replace", got)
	}
}

func TestZonesRejectMalformedDelta(t *testing.T) {
	for name, mutate := range map[string]func(*o.ZonesSnapshot){
		"tick":                func(v *o.ZonesSnapshot) { v.AsOfTick = proto.Int64(2999) },
		"removed and present": func(v *o.ZonesSnapshot) { v.RemovedIds = []string{"Zone_1"} },
		"duplicate tombstone": func(v *o.ZonesSnapshot) { v.RemovedIds = []string{"x", "x"} },
		"missing farm":        func(v *o.ZonesSnapshot) { v.Zones[0].Type = proto.String("growing") },
		"count":               func(v *o.ZonesSnapshot) { v.Completeness.Returned = proto.Uint64(2) },
		"scope": func(v *o.ZonesSnapshot) {
			v.Context.Identity = proto.Clone(pbIdentity()).(*c.Identity)
			v.Context.Identity.LoadToken = proto.String("other")
		},
	} {
		t.Run(name, func(t *testing.T) {
			v := zoneTestPage()
			mutate(v)
			if validateZonePage(v, pbIdentity(), 10) == nil {
				t.Fatal("accepted malformed delta")
			}
		})
	}
	v := zoneTestPage()
	v.Unchanged = proto.Uint32(1)
	if validateZonePage(v, pbIdentity(), 0) == nil {
		t.Fatal("full read admitted unchanged")
	}
}
