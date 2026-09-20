package bridge

import (
	"context"
	"errors"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

func cell(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }

func shrineSnapshot() *o.AncientShrinesSnapshot {
	shrine := &o.AncientShrine{ShrineId: proto.String("ancientTempleApproached-1"), Room: &o.Rectangle{Minimum: cell(10, 10), Maximum: cell(20, 18)}, Sealed: proto.Bool(true), InHome: proto.Bool(false), GuardsKnown: proto.Bool(false),
		Caskets:     []*o.ShrineCasket{{EntityId: proto.String("AncientCryptosleepCasket1"), Cell: cell(12, 12), InteractionCell: cell(13, 12), HitPoints: proto.Uint32(250), MaxHitPoints: proto.Uint32(250), HasContents: proto.Bool(true), PlayerClaimed: proto.Bool(false)}},
		BreachWalls: []*o.ShrineBreachWall{{EntityId: proto.String("Wall7"), Cell: cell(10, 14), Outside: cell(9, 14)}}}
	return &o.AncientShrinesSnapshot{Context: pbContext(), Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}, Shrines: []*o.AncientShrine{shrine}}
}

func TestShrinesReadAndUnavailableStub(t *testing.T) {
	for _, stub := range []bool{false, true} {
		reply := &o.AncientShrinesReply{Outcome: &o.AncientShrinesReply_Observed{Observed: shrineSnapshot()}}
		if stub {
			reply.Outcome = &o.AncientShrinesReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}
		}
		client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
			if arg.Tool != shrinesTool {
				t.Fatal(arg.Tool)
			}
			draftTestRequest(t, arg, &o.AncientShrinesRequest{Scope: &o.ReadScope{ExpectedIdentity: pbIdentity()}})
			return pbResult(reply), nil
		}}, time.Second)
		got, _, err := client.ReadAncientShrines(context.Background(), pbIdentity())
		if stub {
			if !errors.Is(err, ErrUnavailable) {
				t.Fatal(err)
			}
			continue
		}
		if err != nil || !proto.Equal(reply, got) {
			t.Fatal(got, err)
		}
	}
}

func TestShrinesRejectIncompleteAndUnsafeDefaults(t *testing.T) {
	for name, edit := range map[string]func(*o.AncientShrinesSnapshot){
		"wrong load": func(v *o.AncientShrinesSnapshot) { v.Context.Identity.LoadToken = proto.String("other") },
		"partial":    func(v *o.AncientShrinesSnapshot) { v.Completeness.Page.Complete = proto.Bool(false) },
		"unreadable": func(v *o.AncientShrinesSnapshot) { v.Completeness.Unreadable = proto.Uint64(1) },
		"duplicate": func(v *o.AncientShrinesSnapshot) {
			v.Shrines = append(v.Shrines, v.Shrines[0])
			v.Completeness.Matched, v.Completeness.Returned = proto.Uint64(2), proto.Uint64(2)
		},
		"sealed absent":            func(v *o.AncientShrinesSnapshot) { v.Shrines[0].Sealed = nil },
		"home absent":              func(v *o.AncientShrinesSnapshot) { v.Shrines[0].InHome = nil },
		"guards absent":            func(v *o.AncientShrinesSnapshot) { v.Shrines[0].GuardsKnown = nil },
		"sealed with guards known": func(v *o.AncientShrinesSnapshot) { v.Shrines[0].GuardsKnown = proto.Bool(true) },
		"guards while unknown": func(v *o.AncientShrinesSnapshot) {
			v.Shrines[0].Guards = []*o.ShrineGuard{{EntityId: proto.String("Scyther1"), Kind: o.ShrineGuardKind_SHRINE_GUARD_KIND_MECHANOID, Downed: proto.Bool(false), Dead: proto.Bool(false)}}
		},
		"bad room":          func(v *o.AncientShrinesSnapshot) { v.Shrines[0].Room.Maximum.X = proto.Int32(0) },
		"casket hp":         func(v *o.AncientShrinesSnapshot) { v.Shrines[0].Caskets[0].HitPoints = proto.Uint32(300) },
		"casket contents":   func(v *o.AncientShrinesSnapshot) { v.Shrines[0].Caskets[0].HasContents = nil },
		"casket claimed":    func(v *o.AncientShrinesSnapshot) { v.Shrines[0].Caskets[0].PlayerClaimed = nil },
		"wall not adjacent": func(v *o.AncientShrinesSnapshot) { v.Shrines[0].BreachWalls[0].Outside = cell(8, 14) },
		"entity reused": func(v *o.AncientShrinesSnapshot) {
			v.Shrines[0].BreachWalls[0].EntityId = proto.String("AncientCryptosleepCasket1")
		},
		"unknown guard kind": func(v *o.AncientShrinesSnapshot) {
			v.Shrines[0].Sealed, v.Shrines[0].GuardsKnown = proto.Bool(false), proto.Bool(true)
			v.Shrines[0].Guards = []*o.ShrineGuard{{EntityId: proto.String("Scyther1"), Kind: 99, Downed: proto.Bool(false), Dead: proto.Bool(false)}}
		},
	} {
		t.Run(name, func(t *testing.T) {
			v := shrineSnapshot()
			edit(v)
			if ValidateAncientShrines(v, pbIdentity()) == nil {
				t.Fatal("invalid census accepted")
			}
		})
	}
	v := shrineSnapshot()
	v.Shrines[0].Sealed, v.Shrines[0].GuardsKnown = proto.Bool(false), proto.Bool(true)
	v.Shrines[0].Guards = []*o.ShrineGuard{{EntityId: proto.String("Scyther1"), Kind: o.ShrineGuardKind_SHRINE_GUARD_KIND_MECHANOID, Downed: proto.Bool(false), Dead: proto.Bool(true)}}
	if err := ValidateAncientShrines(v, pbIdentity()); err != nil {
		t.Fatal("opened shrine with guards", err)
	}
	v.Shrines = nil
	v.Completeness.Matched, v.Completeness.Returned = proto.Uint64(0), proto.Uint64(0)
	if err := ValidateAncientShrines(v, pbIdentity()); err != nil {
		t.Fatal("complete empty census", err)
	}
}
