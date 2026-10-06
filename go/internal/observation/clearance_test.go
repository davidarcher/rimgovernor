package observation

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type clearanceSource struct {
	reply *o.ClearanceTargetsReply
	err   error
}

func (s clearanceSource) ReadClearanceTargets(context.Context, *c.Identity, bool) (*o.ClearanceTargetsReply, bridge.Result, error) {
	return s.reply, bridge.Result{}, s.err
}

func TestClearanceUnknownEmptyAndChanged(t *testing.T) {
	native := &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(1)}, Tick: proto.Int64(10), NativeGeneration: proto.Uint64(1)}
	expected, err := contextIdentity(native)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := &o.ClearanceTargetsSnapshot{Context: native}
	complete := &o.ClearanceTargetsReply{Outcome: &o.ClearanceTargetsReply_Observed{Observed: snapshot}}
	stub := &o.ClearanceTargetsReply{Outcome: &o.ClearanceTargetsReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}}
	for _, source := range []clearanceSource{{reply: stub}, {err: bridge.ErrUnavailable}} {
		fact, err := ObserveClearanceCensusOnGround(context.Background(), source, expected, true, nil)
		if _, known := fact.Value(); known || err != nil {
			t.Fatal(fact, err)
		}
	}
	fact, err := ObserveClearanceCensusOnGround(context.Background(), clearanceSource{reply: complete}, expected, true, nil)
	if census, known := fact.Value(); !known || len(census.Targets) != 0 || err != nil {
		t.Fatal(fact, err)
	}
	native.NativeGeneration = proto.Uint64(2)
	if _, err := ObserveClearanceCensusOnGround(context.Background(), clearanceSource{reply: complete}, expected, true, nil); !errors.Is(err, ErrChanged) {
		t.Fatal(err)
	}
	transport := errors.New("transport failed")
	if _, err := ObserveClearanceCensusOnGround(context.Background(), clearanceSource{err: transport}, expected, true, nil); !errors.Is(err, transport) {
		t.Fatal(err)
	}
}
