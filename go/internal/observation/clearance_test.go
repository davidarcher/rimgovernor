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

type clearanceSource struct {
	reply *o.ClearanceTargetsReply
	err   error
}

func (s clearanceSource) ReadClearanceTargets(context.Context, *c.Identity) (*o.ClearanceTargetsReply, bridge.Result, error) {
	return s.reply, bridge.Result{}, s.err
}

func TestClearanceUnknownEmptyAndChanged(t *testing.T) {
	native := &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(1)}, Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}
	expected, err := contextIdentity(native)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := &o.ClearanceTargetsSnapshot{Context: native, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(0), Returned: proto.Uint64(0), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}
	complete := &o.ClearanceTargetsReply{Outcome: &o.ClearanceTargetsReply_Observed{Observed: snapshot}}
	stub := &o.ClearanceTargetsReply{Outcome: &o.ClearanceTargetsReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}}
	for _, source := range []clearanceSource{{reply: stub}, {err: bridge.ErrUnavailable}} {
		fact, err := ObserveClearance(context.Background(), source, expected)
		if _, known := fact.Value(); known || err != nil {
			t.Fatal(fact, err)
		}
	}
	fact, err := ObserveClearance(context.Background(), clearanceSource{reply: complete}, expected)
	if rows, known := fact.Value(); !known || len(rows) != 0 || err != nil {
		t.Fatal(fact, err)
	}
	native.NativeGeneration = proto.Uint64(2)
	if _, err := ObserveClearance(context.Background(), clearanceSource{reply: complete}, expected); !errors.Is(err, ErrChanged) {
		t.Fatal(err)
	}
	transport := errors.New("transport failed")
	if _, err := ObserveClearance(context.Background(), clearanceSource{err: transport}, expected); !errors.Is(err, transport) {
		t.Fatal(err)
	}
}
