package observation

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

type shrineSource struct {
	reply *o.AncientShrinesReply
	err   error
}

func (s shrineSource) ReadAncientShrines(context.Context, *c.Identity) (*o.AncientShrinesReply, bridge.Result, error) {
	return s.reply, bridge.Result{}, s.err
}

func TestShrinesUnknownProjectedAndChanged(t *testing.T) {
	native := &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(1)}, Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}
	expected, err := contextIdentity(native)
	if err != nil {
		t.Fatal(err)
	}
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	shrine := &o.AncientShrine{ShrineId: proto.String("shrine1"), Room: &o.Rectangle{Minimum: cell(10, 10), Maximum: cell(20, 18)}, Sealed: proto.Bool(false), InHome: proto.Bool(true), GuardsKnown: proto.Bool(true),
		Caskets:     []*o.ShrineCasket{{EntityId: proto.String("Casket1"), Cell: cell(12, 12), HitPoints: proto.Uint32(40), MaxHitPoints: proto.Uint32(250), HasContents: proto.Bool(true), PlayerClaimed: proto.Bool(false)}, {EntityId: proto.String("Casket2"), Cell: cell(14, 12), HitPoints: proto.Uint32(250), MaxHitPoints: proto.Uint32(250), HasContents: proto.Bool(false), PlayerClaimed: proto.Bool(true)}},
		Guards:      []*o.ShrineGuard{{EntityId: proto.String("Scyther1"), Kind: o.ShrineGuardKind_SHRINE_GUARD_KIND_MECHANOID, Downed: proto.Bool(true), Dead: proto.Bool(false)}},
		BreachWalls: []*o.ShrineBreachWall{{EntityId: proto.String("Wall7"), Cell: cell(10, 14), Outside: cell(9, 14)}}}
	snapshot := &o.AncientShrinesSnapshot{Context: native, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}, Shrines: []*o.AncientShrine{shrine}}
	complete := &o.AncientShrinesReply{Outcome: &o.AncientShrinesReply_Observed{Observed: snapshot}}
	stub := &o.AncientShrinesReply{Outcome: &o.AncientShrinesReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}}
	for _, source := range []shrineSource{{reply: stub}, {err: bridge.ErrUnavailable}} {
		fact, err := ObserveShrines(context.Background(), source, expected)
		if _, known := fact.Value(); known || err != nil {
			t.Fatal(fact, err)
		}
	}
	fact, err := ObserveShrines(context.Background(), shrineSource{reply: complete}, expected)
	rows, known := fact.Value()
	if !known || err != nil || len(rows) != 1 {
		t.Fatal(fact, err)
	}
	got := rows[0]
	if got.ID != "shrine1" || got.Sealed || !got.InHome || !got.GuardsKnown || got.Minimum.X != 10 || got.Maximum.Z != 18 || len(got.Caskets) != 2 || got.FilledCaskets() != 1 || got.Caskets[0].HitPoints != 40 || !got.Caskets[1].PlayerClaimed || len(got.Guards) != 1 || got.Guards[0].Kind != "mechanoid" || !got.Guards[0].Downed || !got.GuardsAlive() || len(got.BreachWalls) != 1 || got.BreachWalls[0].Outside.X != 9 {
		t.Fatalf("%+v", got)
	}
	shrine.Guards[0].Dead = proto.Bool(true)
	fact, _ = ObserveShrines(context.Background(), shrineSource{reply: complete}, expected)
	if rows, _ := fact.Value(); rows[0].GuardsAlive() {
		t.Fatal("dead guard counted alive")
	}
	shrine.Sealed, shrine.GuardsKnown, shrine.Guards = proto.Bool(true), proto.Bool(false), nil
	fact, _ = ObserveShrines(context.Background(), shrineSource{reply: complete}, expected)
	if rows, _ := fact.Value(); !rows[0].GuardsAlive() {
		t.Fatal("sealed shrine must count as guarded")
	}
	native.NativeGeneration = proto.Uint64(2)
	if _, err := ObserveShrines(context.Background(), shrineSource{reply: complete}, expected); !errors.Is(err, ErrChanged) {
		t.Fatal(err)
	}
	transport := errors.New("transport failed")
	if _, err := ObserveShrines(context.Background(), shrineSource{err: transport}, expected); !errors.Is(err, transport) {
		t.Fatal(err)
	}
}
