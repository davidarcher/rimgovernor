package bridge

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func recoveryCensus() *o.ColonyFactsSnapshot {
	context := &c.ObservationContext{Identity: &c.Identity{MapId: proto.Int32(0)}, Tick: proto.Int64(10)}
	b := NewRef("wall")
	return &o.ColonyFactsSnapshot{Context: context, MapSize: &o.MapSize{Width: proto.Uint32(10), Height: proto.Uint32(10)}, Recovery: &o.RecoveryReply{Outcome: &o.RecoveryReply_Observed{Observed: &o.RecoverySnapshot{Context: proto.Clone(context).(*c.ObservationContext), Buildings: []*c.Ref{b}}}}}
}

func TestColonyRecoveryBoundary(t *testing.T) {
	for _, name := range []string{"valid", "stale", "duplicate"} {
		t.Run(name, func(t *testing.T) {
			v := recoveryCensus()
			r := v.Recovery.GetObserved()
			b := r.Buildings[0]
			switch name {
			case "stale":
				r.Context.Tick = proto.Int64(9)
			case "duplicate":
				r.Buildings = append(r.Buildings, NewRef(b.GetId()))
			}
			err := validateColonyRecovery(v)
			if (err == nil) != (name == "valid") {
				t.Fatal(err)
			}
		})
	}
}
