package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func zoneTestPage() *o.ZonesSnapshot {
	ctx := authorityTestContext(3000)
	ctx.Tick = proto.Int64(3000)
	return &o.ZonesSnapshot{Context: ctx, AsOfTick: proto.Int64(3000), Zones: []*o.ZoneState{{Id: proto.String("Zone_1"), FoodStorage: proto.Bool(true), Snapshot: &o.SnapshotRef{Context: ctx, EntityId: proto.String("Zone_1"), Token: proto.String("z")}}}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Returned: proto.Uint64(1), Matched: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}
}

// An ask the native's tombstones cannot answer comes back as the full
// census inline (#795): one round trip, Delta false, so the held census is
// replaced. A refusal is a genuine error and is never retried in full.
func TestZoneUnanswerableAskIsAFullReplyInline(t *testing.T) {
	for _, refuse := range []bool{false, true} {
		t.Run(fmt.Sprint("refuse=", refuse), func(t *testing.T) {
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
				if refuse {
					return pbResult(&o.ListZonesReply{Outcome: &o.ListZonesReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_STALE.Enum()}}}), nil
				}
				page := zoneTestPage()
				page.AsOfTick = nil
				return pbResult(&o.ListZonesReply{Outcome: &o.ListZonesReply_Observed{Observed: page}}), nil
			}}, testBudget)
			got, _, err := client.ReadZoneSection(context.Background(), pbIdentity(), 10)
			if refuse {
				if !errors.Is(err, ErrUnavailable) || len(asks) != 1 {
					t.Fatal(asks, err)
				}
				return
			}
			if err != nil || len(asks) != 1 || asks[0] != 10 || got.Delta || got.AsOf != 3000 || len(got.Rows) != 1 {
				t.Fatal(asks, got, err)
			}
			held := ZonesRead{AsOf: 5, Rows: []*o.ZoneState{{Id: proto.String("Zone_gone")}}}
			if merged := MergeZones(held, got); len(merged.Rows) != 1 || merged.Rows[0].GetId() != "Zone_1" {
				t.Fatal("full reply did not replace the held census", merged)
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
	if got := MergeZones(held, ZonesRead{AsOf: 30}); len(got.Rows) != 0 || got.AsOf != 30 {
		t.Fatal("full read did not replace", got)
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
